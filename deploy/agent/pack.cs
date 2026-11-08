// Builds and signs a Juice Agent package for agent.update.
//   dotnet publish src/Juice.Observability.Agent -c Release -o out/agent
//   dotnet run deploy/agent/pack.cs -- --publish out/agent --key release.key --out packages
// writes packages/juice-agent-{version}.zip and its .sig: ECDSA P-256 with SHA-256, DER, the same as
//   openssl dgst -sha256 -sign release.key -out juice-agent-{version}.zip.sig juice-agent-{version}.zip
// A release key pair, once, kept offline (the private key never goes to the platform or an agent):
//   dotnet run deploy/agent/pack.cs -- --new-key release.key
// writes release.key and release.pub. Hosts trust release.pub in Agent:ReleaseKeyFiles; the platform in Agents:ReleaseKeyFiles.
#:property PublishAot=false
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

string? publish = null, key = null, output = null, newKey = null;
for (var i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--publish": publish = args[i + 1]; break;
        case "--key": key = args[i + 1]; break;
        case "--out": output = args[i + 1]; break;
        case "--new-key": newKey = args[i + 1]; break;
        default: return Usage();
    }
}

if (newKey is not null)
{
    using var pair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    if (File.Exists(newKey)) { Console.Error.WriteLine($"{newKey} exists: not overwriting a key"); return 1; }
    File.WriteAllText(newKey, pair.ExportPkcs8PrivateKeyPem());
    var publicFile = Path.ChangeExtension(newKey, ".pub");
    File.WriteAllText(publicFile, pair.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"wrote {newKey} (private: keep it offline) and {publicFile}");
    return 0;
}
if (publish is null || key is null || output is null) return Usage();

var manifestFile = Path.Combine(publish, "manifest.json");
if (!File.Exists(manifestFile)) { Console.Error.WriteLine($"{manifestFile} is missing: publish the agent project"); return 1; }
using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestFile));
var version = manifest.RootElement.GetProperty("version").GetString()!;
Directory.CreateDirectory(output);
var package = Path.Combine(output, $"juice-agent-{version}.zip");
File.Delete(package);
ZipFile.CreateFromDirectory(publish, package, CompressionLevel.Optimal, includeBaseDirectory: false);
using var signer = ECDsa.Create();
signer.ImportFromPem(File.ReadAllText(key));
var bytes = File.ReadAllBytes(package);
File.WriteAllBytes(package + ".sig", signer.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
Console.WriteLine($"{package}\n  version {version}, {bytes.Length} bytes, sha256 {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
return 0;

static int Usage()
{
    Console.Error.WriteLine("usage: pack.cs --publish DIR --key RELEASE_KEY --out DIR | --new-key FILE");
    return 64;
}
