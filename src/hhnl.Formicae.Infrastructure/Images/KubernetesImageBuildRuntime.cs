using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Application.Integrations;
using hhnl.Formicae.Infrastructure.Kubernetes;
using k8s.Models;
using k8s.Autorest;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.Images;

public sealed class KubernetesImageBuildRuntime(IKubernetesJobApi api, IOptions<ManagedImageOptions> options,
    IOptions<KubernetesJobOptions> jobOptions, RegistryTokens tokens, IHttpClientFactory http,
    IDevOpsIntegrationStore integrations, IGitHubAppClient github) : IImageBuildRuntime
{
    private ManagedImageOptions O => options.Value;
    private string Namespace => string.IsNullOrWhiteSpace(O.BuildNamespace) ? jobOptions.Value.Namespace : O.BuildNamespace;
    public async Task<ImageBuildObservation> ReconcileAsync(ImageBuild build, CancellationToken token)
    {
        ValidateOptions(O);
        var probe = build.State == ImageBuildState.Validating;
        var name = JobName(build.Id, probe);
        V1Job? job = null;
        try { job = await api.ReadJobStatusAsync(name, Namespace, token); }
        catch(HttpOperationException e) when(e.Response.StatusCode == HttpStatusCode.NotFound) { }
        if(job is null)
        {
            if(!probe) await PrepareAsync(build, token);
            try { await api.CreateJobAsync(CreateJob(build, O, Namespace, probe), Namespace, token); }
            catch(HttpOperationException e) when(e.Response.StatusCode == HttpStatusCode.Conflict) { }
            return new(probe ? ImageBuildState.Validating : ImageBuildState.Building, build.Logs, build.Reference);
        }
        var logs = await RedactLogsAsync(build, await LogsAsync(name, token), token);
        if(probe) logs = build.Logs.Split("\nCompatibility probe:\n", 2)[0] + "\nCompatibility probe:\n" + logs;
        if(job.Status?.Failed > 0 || job.Status?.Conditions?.Any(x=>x.Type=="Failed"&&x.Status=="True") == true)
            return new(ImageBuildState.Failed, logs, Failure: probe ? "Image is not compatible with the Formicae worker runtime. Inspect probe logs." : "Dockerfile build or publication failed. Inspect build logs.");
        if(job.Status?.Succeeded > 0)
        {
            if(probe) return new(ImageBuildState.Ready, logs, build.Reference);
            var digest = await ResolveDigestAsync(build, token);
            return new(ImageBuildState.Validating, logs, digest);
        }
        return new(probe ? ImageBuildState.Validating : ImageBuildState.Building, logs, build.Reference);
    }
    private async Task PrepareAsync(ImageBuild build, CancellationToken token)
    {
        var source=ImageJson.Source(build.SourceJson);
        var data=new Dictionary<string,string> { ["buildkitd.toml"] = O.AllowInsecureRegistryForTests ? "[registry.\""+O.Registry+"\"]\n  http = true\n  insecure = true\n" : "" };
        if(source.RepositoryUrl is null) data["Dockerfile"]=source.Dockerfile;
        else data["source"]=source.RepositoryUrl;
        await IgnoreConflict(()=>api.CreateConfigMapAsync(new V1ConfigMap { Metadata=new V1ObjectMeta { Name=JobName(build.Id,false), Labels=Labels(build) }, Data=data },Namespace,token));
        if(O.BundledRegistry)
        {
            var password=tokens.BuildPassword(Repository(build,O));
            var json=JsonSerializer.Serialize(new { auths=new Dictionary<string,object> { [O.Registry]=new { auth=Convert.ToBase64String(Encoding.UTF8.GetBytes("build:"+password)) } } });
            await IgnoreConflict(()=>api.CreateSecretAsync(new V1Secret { Metadata=new V1ObjectMeta { Name=JobName(build.Id,false)+"-push", Labels=Labels(build) },Type="kubernetes.io/dockerconfigjson",StringData=new Dictionary<string,string>{{".dockerconfigjson",json}} },Namespace,token));
        }
        if(source.RepositoryUrl is not null)
        {
            var repo=await integrations.GetRepositoryByUrlAsync(source.RepositoryUrl,token) ?? throw new ArgumentException("Repository is no longer connected.");
            var integration=await integrations.GetAsync(repo.DevOpsIntegrationId,token) ?? throw new ArgumentException("Repository integration is unavailable.");
            var access=repo.InstallationId is { } installation ? await github.CreateInstallationTokenAsync(integration,installation,token) : integration.AccessToken;
            if(string.IsNullOrWhiteSpace(access)) throw new ArgumentException("Repository credentials are unavailable.");
            await IgnoreConflict(()=>api.CreateSecretAsync(new V1Secret { Metadata=new V1ObjectMeta {Name=JobName(build.Id,false)+"-source",Labels=Labels(build)},StringData=new Dictionary<string,string>{{"token",access}} },Namespace,token));
        }
    }
    private async Task<string> ResolveDigestAsync(ImageBuild build, CancellationToken token)
    {
        var client=http.CreateClient("ManagedImageRegistry");
        var repository=Repository(build,O);
        using var request=new HttpRequestMessage(HttpMethod.Head,$"{(O.AllowInsecureRegistryForTests?"http":"https")}://{O.Registry}/v2/{repository}/manifests/build-{build.Id}");
        request.Headers.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.index.v1+json");
        if(O.BundledRegistry) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",tokens.Issue("api",repository,["pull"],TimeSpan.FromMinutes(5)));
        else
        {
            var secret=await api.ReadSecretAsync(O.PushSecretName,Namespace,token);
            if(secret.Data is null || !secret.Data.TryGetValue(".dockerconfigjson",out var data)) throw new InvalidOperationException("Registry credentials are missing.");
            using var document=JsonDocument.Parse(data);
            if(!document.RootElement.GetProperty("auths").TryGetProperty(O.Registry,out var auth) || !auth.TryGetProperty("auth",out var encoded)) throw new InvalidOperationException("Registry credential host does not match.");
            request.Headers.Authorization=new AuthenticationHeaderValue("Basic",encoded.GetString());
        }
        using var response=await client.SendAsync(request,token);
        // External registries can issue a Bearer challenge. Follow their advertised HTTPS token endpoint with the configured credentials.
        if(response.StatusCode==HttpStatusCode.Unauthorized && !O.BundledRegistry)
            return await ResolveExternalDigestAsync(client,request,response,repository,token);
        response.EnsureSuccessStatusCode();
        var digest=response.Headers.TryGetValues("Docker-Content-Digest",out var values)?values.Single():"";
        var reference=O.Registry+"/"+repository+"@"+digest;
        if(!ImageService.IsDigest(reference)) throw new InvalidOperationException("Registry returned an invalid digest.");
        return reference;
    }
    private async Task<string> ResolveExternalDigestAsync(HttpClient client,HttpRequestMessage original,HttpResponseMessage challenge,string repository,CancellationToken token)
    {
        var header=challenge.Headers.WwwAuthenticate.SingleOrDefault(x=>x.Scheme.Equals("Bearer",StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Unsupported registry authentication challenge.");
        var values=System.Text.RegularExpressions.Regex.Matches(header.Parameter??"", "([a-z]+)=\"([^\"]*)\"").ToDictionary(x=>x.Groups[1].Value,x=>x.Groups[2].Value);
        if(!values.TryGetValue("realm",out var realm) || !Uri.TryCreate(realm,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length>0 ||
            (uri.Authority!=O.Registry && !O.ExternalTokenHosts.Contains(uri.Authority,StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("External registry token host must be explicitly configured.");
        var separator=uri.Query.Length==0?"?":"&";
        using var tokenRequest=new HttpRequestMessage(HttpMethod.Get,realm+separator+"service="+Uri.EscapeDataString(values.GetValueOrDefault("service")??"")+"&scope="+Uri.EscapeDataString("repository:"+repository+":pull"));
        tokenRequest.Headers.Authorization=original.Headers.Authorization;
        using var tokenResponse=await client.SendAsync(tokenRequest,token);tokenResponse.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(token));
        var bearer=json.RootElement.TryGetProperty("token",out var entry)?entry.GetString():json.RootElement.GetProperty("access_token").GetString();
        using var retry=new HttpRequestMessage(HttpMethod.Head,original.RequestUri);retry.Headers.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.index.v1+json");
        retry.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        using var response=await client.SendAsync(retry,token);response.EnsureSuccessStatusCode();
        var digest=response.Headers.GetValues("Docker-Content-Digest").Single();var reference=O.Registry+"/"+repository+"@"+digest;
        if(!ImageService.IsDigest(reference))throw new InvalidOperationException("Registry returned an invalid digest.");return reference;
    }
    private async Task<string> LogsAsync(string job,CancellationToken token)
    {
        var pods=await api.ListPodsAsync(Namespace,"job-name="+job,token);
        var output=new StringBuilder();
        foreach(var pod in pods.Take(2)) foreach(var container in (pod.Spec.InitContainers??[]).Concat(pod.Spec.Containers))
        {
            try { output.AppendLine(await api.ReadPodLogAsync(pod.Metadata.Name,Namespace,container.Name,token)); }
            catch(HttpOperationException e) when(e.Response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) { }
        }
        var text=output.ToString();return text.Length>65536?text[^65536..]:text;
    }
    private async Task<string> RedactLogsAsync(ImageBuild build,string logs,CancellationToken token)
    {
        var names = new[] { JobName(build.Id,false)+"-source", O.BundledRegistry ? JobName(build.Id,false)+"-push" : O.PushSecretName };
        foreach(var name in names.Where(x=>!string.IsNullOrWhiteSpace(x))) {
            V1Secret secret;
            try { secret=await api.ReadSecretAsync(name,Namespace,token); }
            catch(HttpOperationException e) when(e.Response.StatusCode==HttpStatusCode.NotFound) { continue; }
            foreach(var item in secret.Data??new Dictionary<string,byte[]>()) {
                var value=Encoding.UTF8.GetString(item.Value);
                if(item.Key==".dockerconfigjson") {
                    using var json=JsonDocument.Parse(value);
                    foreach(var registry in json.RootElement.GetProperty("auths").EnumerateObject()) {
                        if(registry.Value.TryGetProperty("auth",out var auth)) {
                            var encoded=auth.GetString()!;logs=logs.Replace(encoded,"***",StringComparison.Ordinal);
                            var pair=Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split(':',2);
                            if(pair.Length==2 && pair[1].Length>0)logs=logs.Replace(pair[1],"***",StringComparison.Ordinal);
                        }
                    }
                } else if(value.Length>0) logs=logs.Replace(value,"***",StringComparison.Ordinal);
            }
        }
        return logs;
    }
    public async Task CleanupAsync(ImageBuild build,CancellationToken token)
    {
        foreach(var name in new[]{JobName(build.Id,false),JobName(build.Id,true)}) await IgnoreNotFound(()=>api.DeleteJobAsync(name,Namespace,token));
        await IgnoreNotFound(()=>api.DeleteConfigMapAsync(JobName(build.Id,false),Namespace,token));
        foreach(var suffix in new[]{"-push","-source"}) await IgnoreNotFound(()=>api.DeleteSecretAsync(JobName(build.Id,false)+suffix,Namespace,token));
    }
    private static async Task IgnoreConflict(Func<Task> action) { try{await action();}catch(HttpOperationException e)when(e.Response.StatusCode==HttpStatusCode.Conflict){} }
    private static async Task IgnoreNotFound(Func<Task> action) { try{await action();}catch(HttpOperationException e)when(e.Response.StatusCode==HttpStatusCode.NotFound){} }
    public static string JobName(string id,bool probe)=>"formicae-image-"+id+(probe?"-probe":"");
    public static string Repository(ImageBuild b,ManagedImageOptions o)=>o.RepositoryPrefix+"/"+b.ImageId;
    private static Dictionary<string,string> Labels(ImageBuild b)=>new(){{"app.kubernetes.io/managed-by","formicae"},{"formicae-image-build",b.Id}};
    public static void ValidateOptions(ManagedImageOptions o)
    {
        if(!o.Enabled) throw new InvalidOperationException("Managed image builds are disabled.");
        if(!System.Text.RegularExpressions.Regex.IsMatch(o.Registry,"^[a-z0-9][a-z0-9.-]*(?::[0-9]{1,5})?$")) throw new InvalidOperationException("Configure a node-reachable registry hostname.");
        if(!System.Text.RegularExpressions.Regex.IsMatch(o.RepositoryPrefix,"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$")) throw new InvalidOperationException("Registry repository prefix is invalid.");
        if(string.IsNullOrWhiteSpace(o.WorkerBaseImage) || o.PullSecretNames.Length==0 || (!o.BundledRegistry&&string.IsNullOrWhiteSpace(o.PushSecretName))) throw new InvalidOperationException("Configure worker base and registry push/pull credentials.");
    }
    public static V1Job CreateJob(ImageBuild build,ManagedImageOptions o,string ns,bool probe)
    {
        var source=ImageJson.Source(build.SourceJson);var name=JobName(build.Id,probe);
        var volumes=new List<V1Volume>();var containers=new List<V1Container>();var init=new List<V1Container>();
        if(probe)
            containers.Add(new V1Container {Name="probe",Image=build.Reference,ImagePullPolicy="Always",Command=["dotnet","hhnl.Formicae.Worker.dll","--check-runtime"],Resources=Resources("100m","512Mi","1","1Gi")});
        else
        {
            volumes.AddRange([new V1Volume{Name="context",EmptyDir=new V1EmptyDirVolumeSource{SizeLimit=new ResourceQuantity("256Mi")}},new V1Volume{Name="dockerfile",EmptyDir=new()},
                new V1Volume{Name="builder-config",ConfigMap=new V1ConfigMapVolumeSource{Name=name,Items=[new V1KeyToPath{Key="buildkitd.toml",Path="buildkitd.toml"}]}},new V1Volume{Name="input",ConfigMap=new V1ConfigMapVolumeSource{Name=name}},new V1Volume{Name="cache",EmptyDir=new V1EmptyDirVolumeSource{SizeLimit=new ResourceQuantity("8Gi")}},
                new V1Volume{Name="push",Secret=new V1SecretVolumeSource{SecretName=o.BundledRegistry?name+"-push":o.PushSecretName,Items=[new V1KeyToPath{Key=".dockerconfigjson",Path="config.json"}]}}]);
            var prepare=source.RepositoryUrl is null?"cp /input/Dockerfile /dockerfile/Dockerfile":RepositoryPreparation(source,build);
            init.Add(new V1Container {Name="prepare",Image=o.WorkerBaseImage,Command=["/bin/sh","-ec",prepare],Resources=Resources("100m","256Mi","1","512Mi"),
                Env=source.RepositoryUrl is null?[]:[new V1EnvVar{Name="SOURCE_TOKEN",ValueFrom=new V1EnvVarSource{SecretKeyRef=new V1SecretKeySelector{Name=name+"-source",Key="token"}}}],
                VolumeMounts=[new(){Name="context",MountPath="/context"},new(){Name="dockerfile",MountPath="/dockerfile"},new(){Name="input",MountPath="/input",ReadOnlyProperty=true}]});
            var args=new List<string>{"buildctl-daemonless.sh","build","--frontend","dockerfile.v0","--local","context=/context","--local","dockerfile=/dockerfile","--opt","platform="+source.Platform,"--output","type=image,name="+o.Registry+"/"+Repository(build,o)+":build-"+build.Id+",push=true"+(o.AllowInsecureRegistryForTests?",registry.insecure=true":"")};
            if(source.Target is not null) args.AddRange(["--opt","target="+source.Target]);
            foreach(var pair in source.BuildArguments??new Dictionary<string,string>()) args.AddRange(["--opt","build-arg:"+pair.Key+"="+pair.Value]);
            containers.Add(new V1Container {Name="build",Image=o.BuilderImage,Command=args,Resources=Resources("250m","512Mi","2","2Gi"),
                Env=[new(){Name="DOCKER_CONFIG",Value="/credentials"},new(){Name="BUILDKITD_FLAGS",Value="--oci-worker-no-process-sandbox --config=/etc/buildkit/buildkitd.toml"}],
                SecurityContext=new V1SecurityContext{RunAsUser=1000,RunAsGroup=1000,SeccompProfile=new V1SeccompProfile{Type="Unconfined"},AppArmorProfile=new V1AppArmorProfile{Type="Unconfined"}},
                VolumeMounts=[new(){Name="context",MountPath="/context",ReadOnlyProperty=true},new(){Name="dockerfile",MountPath="/dockerfile",ReadOnlyProperty=true},new(){Name="push",MountPath="/credentials",ReadOnlyProperty=true},new(){Name="builder-config",MountPath="/etc/buildkit",ReadOnlyProperty=true},new(){Name="cache",MountPath="/home/user/.local/share/buildkit"}]});
        }
        return new V1Job {Metadata=new V1ObjectMeta{Name=name,NamespaceProperty=ns,Labels=Labels(build)},Spec=new V1JobSpec{BackoffLimit=0,ActiveDeadlineSeconds=o.TimeoutSeconds,TtlSecondsAfterFinished=86400,
            Template=new V1PodTemplateSpec{Metadata=new V1ObjectMeta{Labels=Labels(build)},Spec=new V1PodSpec{RestartPolicy="Never",AutomountServiceAccountToken=false,ServiceAccountName=o.BuildServiceAccount.Length==0?null:o.BuildServiceAccount,
                NodeSelector=o.NodeSelector.Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]),SecurityContext=new V1PodSecurityContext{FsGroup=1000},
                ImagePullSecrets=o.PullSecretNames.Select(x=>new V1LocalObjectReference{Name=x}).ToList(),Volumes=volumes,Containers=containers,InitContainers=init}}}};
    }
    private static V1ResourceRequirements Resources(string cpu,string memory,string limitCpu,string limitMemory)=>new(){Requests=new Dictionary<string,ResourceQuantity>{{"cpu",new(cpu)},{"memory",new(memory)}},Limits=new Dictionary<string,ResourceQuantity>{{"cpu",new(limitCpu)},{"memory",new(limitMemory)},{"ephemeral-storage",new("10Gi")}}};
    private static string Q(string text)=>"'"+text.Replace("'","'\"'\"'")+"'";
    private static string RepositoryPreparation(ImageSource s,ImageBuild b)=>$"""
        set -eu
        export GIT_TERMINAL_PROMPT=0
        printf '%s\n' '#!/bin/sh' 'case "$1" in *Username*) printf "x-access-token";; *) printf "%s" "$SOURCE_TOKEN";; esac' > /tmp/askpass
        chmod 700 /tmp/askpass
        export GIT_ASKPASS=/tmp/askpass
        mkdir /tmp/repo
        git -C /tmp/repo init -q
        git -C /tmp/repo fetch --depth=1 {Q(s.RepositoryUrl!)} {Q(b.CommitSha!)}
        git -C /tmp/repo checkout --detach FETCH_HEAD
        context=$(realpath /tmp/repo/{Q(s.ContextPath)})
        dockerfile=$(realpath /tmp/repo/{Q(s.DockerfilePath)})
        case "$context/" in /tmp/repo/*) ;; *) exit 1;; esac
        case "$dockerfile" in /tmp/repo/*) ;; *) exit 1;; esac
        test -d "$context" && test -f "$dockerfile"
        test $(du -sk "$context" | cut -f1) -le 204800
        cp "$dockerfile" /dockerfile/Dockerfile
        cp -a "$context/." /context/
        rm -rf /context/.git
        """;
}
