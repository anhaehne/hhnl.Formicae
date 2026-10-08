using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Infrastructure.Images;

/// <summary>Registry-native scoped JWTs; credential values never enter workflow definitions.</summary>
public sealed class RegistryTokens(IOptions<ManagedImageOptions> options)
{
    private ManagedImageOptions O => options.Value;
    public string Issue(string subject, string repository, string[] actions, TimeSpan lifetime)
    {
        using var cert = X509Certificate2.CreateFromPemFile(O.SigningCertificatePath, O.SigningKeyPath);
        var key = new X509SecurityKey(cert);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = O.TokenIssuer, Audience = O.TokenService, IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddSeconds(-30), Expires = DateTime.UtcNow.Add(lifetime),
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = Guid.NewGuid().ToString("N"),
                ["access"] = JsonSerializer.SerializeToElement(new[] { new { type = "repository", name = repository, actions } }) },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            AdditionalHeaderClaims = new Dictionary<string, object> { ["x5c"] = new[] { Convert.ToBase64String(cert.RawData) } }
        });
    }
    public async Task<string?> AuthorizeAsync(string authorization, string service, string[] scopes)
    {
        if (!O.Enabled || !O.BundledRegistry || service != O.TokenService || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        string user, password;
        try {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..])).Split(':',2);
            if(pair.Length!=2) return null; user=pair[0]; password=pair[1];
        } catch(FormatException) { return null; }
        string? permittedRepository = null;
        var canPush = false;
        if(user == "agent" && O.PullPassword.Length >= 24 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), SHA256.HashData(Encoding.UTF8.GetBytes(O.PullPassword)))) { }
        else if(user == "build") {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(O.SigningCertificatePath));
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(password, new TokenValidationParameters {
                ValidIssuer=O.TokenIssuer, ValidAudience=O.TokenService, IssuerSigningKey=new X509SecurityKey(cert),
                ValidateIssuer=true, ValidateAudience=true, ValidateLifetime=true, ValidateIssuerSigningKey=true, ClockSkew=TimeSpan.FromSeconds(30)
            });
            if(!result.IsValid || result.SecurityToken is not JsonWebToken jwt || !jwt.TryGetPayloadValue<string>("buildRepository", out permittedRepository)) return null;
            canPush=true;
        } else return null;
        if(scopes.Length>1) return null;
        if(scopes.Length==0) return Issue(user, "", [], TimeSpan.FromMinutes(5));
        var parts=scopes[0].Split(':',3);
        if(parts.Length!=3 || parts[0]!="repository" || !parts[1].StartsWith(O.RepositoryPrefix+"/",StringComparison.Ordinal) || parts[1].Contains("..")) return null;
        if(canPush && permittedRepository!=parts[1]) return null;
        var requested=parts[2].Split(',');
        var granted=requested.Where(x=>x=="pull" || (x=="push" && canPush)).Distinct().ToArray();
        return Issue(user,parts[1],granted,TimeSpan.FromMinutes(5));
    }
    public string BuildPassword(string repository)
    {
        using var cert=X509Certificate2.CreateFromPemFile(O.SigningCertificatePath,O.SigningKeyPath);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer=O.TokenIssuer,Audience=O.TokenService,Expires=DateTime.UtcNow.AddSeconds(O.TimeoutSeconds+300),
            Claims=new Dictionary<string,object> { ["sub"]="build", ["buildRepository"]=repository },
            SigningCredentials=new SigningCredentials(new X509SecurityKey(cert),SecurityAlgorithms.RsaSha256)
        });
    }
}
