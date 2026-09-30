using System.Diagnostics;
using System.Xml.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Radar;

public sealed class ProtoCompiler
{
    private readonly string protoc;
    private readonly string wellKnownTypes;
    private readonly string tempDirectory;

    public ProtoCompiler(string repoRoot)
    {
        string props = Path.Combine(repoRoot, "Directory.Packages.props");
        string version =
            XDocument
                .Load(props)
                .Descendants()
                .Single(node =>
                    node.Name.LocalName == "PackageVersion"
                    && (string?)node.Attribute("Include") == "Grpc.Tools"
                )
                .Attribute("Version")
                ?.Value
            ?? throw new FormatException("Grpc.Tools version is missing.");
        string packages =
            Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages"
            );
        string package = Path.Combine(packages, "grpc.tools", version);
        protoc = Path.Combine(package, "tools", "windows_x64", "protoc.exe");
        wellKnownTypes = Path.Combine(package, "build", "native", "include");
        tempDirectory = Path.Combine(repoRoot, "data", "cache", "tmp");
        if (!File.Exists(protoc) || !Directory.Exists(wellKnownTypes))
            throw new FileNotFoundException(
                $"Grpc.Tools protoc or include directory not found under {package}."
            );
    }

    public byte[] Compile(string protoRoot, string version, out int fileCount)
    {
        string versionDirectory = Path.Combine(protoRoot, "google", "ads", "googleads", version);
        if (!Directory.Exists(versionDirectory))
            throw new DirectoryNotFoundException(
                $"Google Ads {version} proto directory does not exist at this ref: {versionDirectory}"
            );
        var files = Directory
            .GetFiles(versionDirectory, "*.proto", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(protoRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            throw new FormatException($"Google Ads {version} proto directory has no .proto files.");
        fileCount = files.Length;
        Directory.CreateDirectory(tempDirectory);
        string temp = Path.Combine(tempDirectory, ".radar-" + Guid.NewGuid().ToString("N") + ".pb");
        try
        {
            var merged = new FileDescriptorSet();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Windows has a command-line length limit; each invocation emits its imports too.
            foreach (var batch in files.Chunk(50))
            {
                var start = new ProcessStartInfo(protoc)
                {
                    WorkingDirectory = protoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("--include_imports");
                start.ArgumentList.Add("--include_source_info");
                start.ArgumentList.Add("--proto_path=" + protoRoot);
                start.ArgumentList.Add("--proto_path=" + wellKnownTypes);
                start.ArgumentList.Add("--descriptor_set_out=" + temp);
                foreach (string file in batch)
                    start.ArgumentList.Add(file);
                using var process =
                    Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start protoc.");
                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                process.WaitForExit();
                string stderr = stderrTask.GetAwaiter().GetResult();
                string stdout = stdoutTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"protoc failed for {version} (exit {process.ExitCode}): {stderr} {stdout}".Trim()
                    );
                var descriptor = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(temp));
                foreach (var file in descriptor.File)
                    if (seen.Add(file.Name))
                        merged.File.Add(file);
            }
            if (
                !merged.File.Any(file =>
                    file.Package == $"google.ads.googleads.{version}.resources"
                )
            )
                throw new FormatException(
                    $"Descriptor set for {version} has no matching resources package."
                );
            return merged.ToByteArray();
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
