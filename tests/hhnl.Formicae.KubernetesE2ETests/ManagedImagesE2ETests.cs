using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using hhnl.Formicae.Application.Images;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Images;
using hhnl.Formicae.KubernetesE2ETests.Infrastructure;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed partial class KubernetesWorkflowE2ETests
{
    [Fact]
    public Task Managed_image_is_built_pushed_probed_pinned_and_pulled_for_agent_task()
        => WithDiagnosticsAsync(async () =>
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(15));var token=deadline.Token;
            using var client=new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(fixture.KubeconfigPath));
            var node=(await client.CoreV1.ListNodeAsync(cancellationToken:token)).Items.Single().Metadata.Name;
            var docker=fixture.ContainerCli;
            async Task<CommandResult> Docker(params string[] args)=>await CommandRunner.RunRequiredAsync(docker,args,fixture.RepositoryRoot,TimeSpan.FromMinutes(5),cancellationToken:token);
            var nodeIp=(await Docker("inspect","--format","{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}",node)).StandardOutput.Trim();Assert.True(IPAddress.TryParse(nodeIp,out _));
            var name="formicae-managed-registry-"+Guid.NewGuid().ToString("N")[..8];
            var root=Path.Combine(fixture.TempRoot,name);Directory.CreateDirectory(root);
            using var rsa=RSA.Create(2048);using var cert=new CertificateRequest("CN=Formicae E2E signing",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(2));
            var certPem=cert.ExportCertificatePem();var keyPem=rsa.ExportPkcs8PrivateKeyPem();var pullPassword=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllTextAsync(Path.Combine(root,"cert.crt"),certPem,token);await File.WriteAllTextAsync(Path.Combine(root,"key.key"),keyPem,token);
            var signing="image-e2e-signing";var pull="image-e2e-pull";
            var original=await client.AppsV1.ReadNamespacedDeploymentAsync("formicae-api","formicae",cancellationToken:token);
            var originalTemplate=JsonSerializer.Serialize(original.Spec.Template);
            try
            {
                await client.CoreV1.CreateNamespacedSecretAsync(new V1Secret{Metadata=new(){Name=signing},StringData=new Dictionary<string,string>{{"cert.crt",certPem},{"key.key",keyPem}}},"formicae",cancellationToken:token);
                await client.CoreV1.CreateNamespacedServiceAsync(new V1Service{Metadata=new(){Name="image-e2e-token"},Spec=new(){Type="NodePort",Selector=original.Spec.Selector.MatchLabels,Ports=[new(){Port=80,TargetPort=8080,NodePort=30080}]}},"formicae",cancellationToken:token);
                await Docker("create","--name",name,"--network","kind","-p","127.0.0.1::5000","-e","REGISTRY_AUTH=token","-e","REGISTRY_AUTH_TOKEN_REALM=http://"+nodeIp+":30080/api/registry/token","-e","REGISTRY_AUTH_TOKEN_SERVICE=formicae-registry","-e","REGISTRY_AUTH_TOKEN_ISSUER=formicae-images","-e","REGISTRY_AUTH_TOKEN_ROOTCERTBUNDLE=/cert.crt","registry:3.0.0");
                await Docker("cp",Path.Combine(root,"cert.crt"),name+":/cert.crt");await Docker("start",name);
                var registryIp=(await Docker("inspect","--format","{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}",name)).StandardOutput.Trim();Assert.True(IPAddress.TryParse(registryIp,out _));var registry=registryIp+":5000";
                var localRegistry=(await Docker("port",name,"5000/tcp")).StandardOutput.Trim();Assert.StartsWith("127.0.0.1:",localRegistry);
                var options=new ManagedImageOptions{Enabled=true,Registry=registry,WorkerBaseImage=fixture.WorkerImage,PullSecretNames=[pull],SigningCertificatePath=Path.Combine(root,"cert.crt"),SigningKeyPath=Path.Combine(root,"key.key")};
                var issuer=new RegistryTokens(Options.Create(options));
                var pullJson=JsonSerializer.Serialize(new{auths=new Dictionary<string,object>{{registry,new{auth=Convert.ToBase64String(Encoding.UTF8.GetBytes("agent:"+pullPassword))}}}});
                await client.CoreV1.CreateNamespacedSecretAsync(new V1Secret{Metadata=new(){Name=pull},Type="kubernetes.io/dockerconfigjson",StringData=new Dictionary<string,string>{{".dockerconfigjson",pullJson}}},"formicae",cancellationToken:token);
                var api=original;var container=api.Spec.Template.Spec.Containers.Single();
                var env=new Dictionary<string,string>{{"ManagedImages__Enabled","true"},{"ManagedImages__BundledRegistry","true"},{"ManagedImages__Registry",registry},{"ManagedImages__RepositoryPrefix","formicae/local"},{"ManagedImages__BuildNamespace","formicae"},{"ManagedImages__WorkerBaseImage",fixture.WorkerImage},{"ManagedImages__PullSecretNames__0",pull},{"ManagedImages__SigningCertificatePath","/image-signing/cert.crt"},{"ManagedImages__SigningKeyPath","/image-signing/key.key"},{"ManagedImages__PullPassword",pullPassword},{"ManagedImages__AllowInsecureRegistryForTests","true"}};
                container.Env??=[];foreach(var pair in env)container.Env.Add(new(){Name=pair.Key,Value=pair.Value});
                api.Spec.Template.Spec.Volumes??=[];api.Spec.Template.Spec.Volumes.Add(new(){Name="image-signing",Secret=new(){SecretName=signing}});container.VolumeMounts??=[];container.VolumeMounts.Add(new(){Name="image-signing",MountPath="/image-signing",ReadOnlyProperty=true});
                await client.AppsV1.ReplaceNamespacedDeploymentAsync(api,"formicae-api","formicae",cancellationToken:token);
                await CommandRunner.RunRequiredAsync("kubectl",["--kubeconfig",fixture.KubeconfigPath,"rollout","status","deployment/formicae-api","-n","formicae","--timeout=180s"],fixture.RepositoryRoot,TimeSpan.FromMinutes(4),cancellationToken:token);
                var dockerConfig=Path.Combine(root,"docker-config");Directory.CreateDirectory(dockerConfig);
                await File.WriteAllTextAsync(Path.Combine(dockerConfig,"config.json"),JsonSerializer.Serialize(new{auths=new Dictionary<string,object>{{localRegistry,new{auth=Convert.ToBase64String(Encoding.UTF8.GetBytes("build:"+issuer.BuildPassword("formicae/local/base")))}}}}),token);
                await Docker("tag",fixture.WorkerImage,localRegistry+"/formicae/local/base:worker");await Docker("--config",dockerConfig,"push",localRegistry+"/formicae/local/base:worker");
                // Node containerd, rather than a pod, performs the private image pull. HTTP is confined to this disposable E2E registry.
                await Docker("exec",node,"sh","-ec","mkdir -p /etc/containerd/certs.d/"+registry+"; printf '%s\\n' 'server = \"http://"+registry+"\"' '[host.\"http://"+registry+"\"]' 'capabilities = [\"pull\", \"resolve\"]' > /etc/containerd/certs.d/"+registry+"/hosts.toml");
                using var forward=await fixture.StartApiPortForwardAsync();using var http=new HttpClient{BaseAddress=forward.BaseAddress};
                var dockerfile=$"""
                    FROM {registry}/formicae/local/base:worker
                    RUN printf '#!/bin/sh\nprintf prepared-image-tool\n' > /usr/local/bin/formicae-image-tool && chmod 755 /usr/local/bin/formicae-image-tool
                    RUN printf '#!/bin/sh\nformicae-image-tool\n' > /root/.local/bin/openhands && chmod 755 /root/.local/bin/openhands
                    """;
                var created=await http.PostAsJsonAsync("/api/images",new ImageRequest("E2E prepared tools",null,new(dockerfile)),token);created.EnsureSuccessStatusCode();var image=(await created.Content.ReadFromJsonAsync<ImageResponse>(token))!;
                var queued=await http.PostAsJsonAsync($"/api/images/{image.Id}/builds",new{expectedRevision=1},token);Assert.Equal(HttpStatusCode.Accepted,queued.StatusCode);var build=(await queued.Content.ReadFromJsonAsync<ImageBuildResponse>(token))!;
                async Task<ImageBuildResponse> Wait(string id) {
                    while(true){var current=(await http.GetFromJsonAsync<ImageBuildResponse>($"/api/images/{image.Id}/builds/{id}",token))!;if(current.State is "Ready" or "Failed" or "TimedOut" or "Cancelled")return current;await Task.Delay(1000,token);}
                }
                build=await Wait(build.Id);Assert.True(build.State=="Ready",build.Failure+"\n"+build.Logs);Assert.True(ImageService.IsDigest(build.Reference!));
                Assert.DoesNotContain(pullPassword,build.Logs);Assert.DoesNotContain(keyPem,build.Logs);
                await using var context=new ExtensionWorkerContext(fixture.KubeconfigPath,fixture.WorkerImage,OpenHandsAuthMethods.ApiKey);
                var configuration=new EnvironmentConfiguration{Image=new(build.Reference!,"IfNotPresent",[pull]),Runtime=new(60)};
                var task=new AgentTask(Guid.NewGuid(),TaskRunKind.Custom,"Use prepared tools","https://example.invalid/repo","main",null,ExecutionAttemptId:Guid.NewGuid(),TimeoutSeconds:60,EnvironmentSnapshot:context.Snapshot(configuration),Capabilities:[]);
                var started=await context.StartAsync(task);var result=await context.WaitAsync(started.ExternalId);Assert.True(result.Succeeded,result.FailureReason);Assert.Contains("prepared-image-tool",result.Output);
                var job=await context.Api.ReadJobStatusAsync(started.ExternalId,"formicae",token);Assert.Equal(build.Reference,job.Spec.Template.Spec.Containers.Single().Image);Assert.Equal(pull,job.Spec.Template.Spec.ImagePullSecrets.Single().Name);
                var edited=await http.PutAsJsonAsync("/api/images/"+image.Id,new ImageRequest(image.Name,null,new(dockerfile+"\nRUN exit 3"),1),token);edited.EnsureSuccessStatusCode();
                queued=await http.PostAsJsonAsync($"/api/images/{image.Id}/builds",new{expectedRevision=2},token);queued.EnsureSuccessStatusCode();var failed=await Wait((await queued.Content.ReadFromJsonAsync<ImageBuildResponse>(token))!.Id);Assert.Equal("Failed",failed.State);
                Assert.Equal(build.Reference,task.EnvironmentSnapshot!.Configuration.Image!.Reference);
                // Retried execution still uses the successful digest after a failed rebuild.
                started=await context.StartAsync(task with {ExecutionAttemptId=Guid.NewGuid()});result=await context.WaitAsync(started.ExternalId);Assert.True(result.Succeeded,result.FailureReason);Assert.Contains("prepared-image-tool",result.Output);
            }
            finally
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var deployment=await client.AppsV1.ReadNamespacedDeploymentAsync("formicae-api","formicae",cancellationToken:cleanup.Token);
                deployment.Spec.Template=JsonSerializer.Deserialize<V1PodTemplateSpec>(originalTemplate)!;
                await client.AppsV1.ReplaceNamespacedDeploymentAsync(deployment,"formicae-api","formicae",cancellationToken:cleanup.Token);
                await CommandRunner.RunAsync(docker,["rm","-f",name],fixture.RepositoryRoot,TimeSpan.FromSeconds(30));
                Directory.Delete(root,true);
            }
        });
}
