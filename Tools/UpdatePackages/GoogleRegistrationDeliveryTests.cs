using FMSC_SMS.Core.Updates;
using System.Security.Cryptography;
using System.Text.Json;

internal static class GoogleRegistrationDeliveryTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "fmsc-registration-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string id = "fictional-desktop.apps.googleusercontent.com";
        string json = JsonSerializer.Serialize(new { installed = new { client_id = id, client_secret = "fictional-native-value" } });
        void Check(bool okay, string reason) { if (!okay) throw new Exception(reason); }
        void Refused(Action action)
        {
            try { action(); }
            catch (InvalidDataException error) {
                Check(!error.ToString().Contains("fictional-native-value"), "Native metadata leaked in a publication error."); return;
            }
            throw new Exception("Invalid or missing private registration was accepted.");
        }
        try
        {
            string original = Path.Combine(root,"public-candidate"); Directory.CreateDirectory(original);
            File.WriteAllText(Path.Combine(original,"FMSC_SMS.exe"),"fictional executable, never run");
            File.WriteAllText(Path.Combine(original,"CloudBackupProviders.json"),JsonSerializer.Serialize(new { GoogleClientId = id }));
            File.WriteAllText(Path.Combine(original,"SHA256SUMS.txt"),"sealed public build inventory");
            Refused(() => GoogleRegistrationDelivery.Require(original));
            Refused(() => GoogleRegistrationDelivery.Prepare(original,null));
            var sealedFiles = Directory.GetFiles(original).ToDictionary(p => Path.GetFileName(p), p => SHA256.HashData(File.ReadAllBytes(p)));
            string application = Path.Combine(root,"private-application"); Directory.CreateDirectory(application);
            foreach (string file in Directory.GetFiles(original)) File.Copy(file,Path.Combine(application,Path.GetFileName(file)));
            GoogleRegistrationDelivery.Prepare(application,json);
            GoogleRegistrationDelivery.Require(application);
            Check(File.ReadAllText(Path.Combine(application,GoogleRegistrationDelivery.FileName)) == json,
                "Private payload lost its validated Desktop registration.");
            Check(File.ReadAllText(Path.Combine(application,"SHA256SUMS.txt")).Contains("  " + GoogleRegistrationDelivery.FileName),
                "Private inventory must include the added registration.");
            foreach (var entry in sealedFiles)
                Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(original,entry.Key!))).SequenceEqual(entry.Value),
                    "The sealed public candidate must remain unchanged.");
            Check(!File.Exists(Path.Combine(original,GoogleRegistrationDelivery.FileName)), "Registration leaked into the public candidate.");
            foreach (string bad in new[] { "{", "{}", "{\"web\":{}}", json.Replace(id,"wrong-client"), new string('x',16385) })
            {
                Refused(() => GoogleRegistrationDelivery.Prepare(application,bad));
                Check(File.ReadAllText(Path.Combine(application,GoogleRegistrationDelivery.FileName)) == json,
                    "Invalid replacement must preserve the previous registration.");
            }
            string package = Path.Combine(root,"package");
            var manifest = PackageBuilder.Build(application,package,"1.1.0",1,original,"1.0.0");
            Check(manifest.Files.Any(f => f.Path == GoogleRegistrationDelivery.FileName) &&
                manifest.ChangedFiles.Contains(GoogleRegistrationDelivery.FileName),
                "Full and incremental updates must both deliver newly introduced Google registration.");
            UpdatePackageVerification.Verify(package,original,Path.Combine(root,"verified"));
            string extracted = UpdatePackageVerification.ExtractFull(package,Path.Combine(root,"extracted"));
            Check(File.ReadAllText(Path.Combine(extracted,GoogleRegistrationDelivery.FileName)) == json,
                "A verified full update failed to reconstruct the exact registration bytes.");
            string local = Path.Combine(root,"local"); Directory.CreateDirectory(local);
            File.Copy(Path.Combine(original,"CloudBackupProviders.json"),Path.Combine(local,"CloudBackupProviders.json"));
            File.WriteAllText(Path.Combine(local,GoogleRegistrationDelivery.FileName),json);
            GoogleRegistrationDelivery.Prepare(local,null);
            Check(File.ReadAllText(Path.Combine(local,GoogleRegistrationDelivery.FileName)) == json,
                "Local publishing must accept an already supplied matching registration without an Actions secret.");
            Console.WriteLine("PASS: private registration validation, public candidate isolation, full/delta delivery and local publish metadata.");
        }
        finally { Directory.Delete(root,recursive:true); }
    }
}
