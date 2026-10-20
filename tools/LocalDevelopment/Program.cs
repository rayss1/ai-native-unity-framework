using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

// Keep OS process timestamps identical to LocalDebugBootstrap's .NET representation.
try
{
    switch (args)
    {
        case ["identity", string pid]:
            using (
                Process process = Process.GetProcessById(
                    int.Parse(pid, CultureInfo.InvariantCulture)
                )
            )
            {
                if (process.HasExited)
                    return 3;
                Console.WriteLine(
                    JsonSerializer.Serialize(
                        new
                        {
                            Pid = process.Id,
                            StartUtc = process
                                .StartTime.ToUniversalTime()
                                .ToString("O", CultureInfo.InvariantCulture),
                            Executable = process.MainModule?.FileName
                                ?? throw new InvalidOperationException("process-path-unavailable"),
                        }
                    )
                );
            }
            return 0;
        case ["keys", string privateFile, string publicFile]:
            using (RSA rsa = RSA.Create(2048))
            {
                if (File.Exists(privateFile))
                    rsa.ImportFromPem(File.ReadAllText(privateFile));
                else
                {
                    using FileStream output = new(
                        privateFile,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    );
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(
                            privateFile,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite
                        );
                    using StreamWriter writer = new(output);
                    writer.Write(rsa.ExportPkcs8PrivateKeyPem());
                }
                File.WriteAllText(publicFile, rsa.ExportSubjectPublicKeyInfoPem());
            }
            return 0;
        default:
            Console.Error.WriteLine(
                "usage: LocalDevelopment identity PID | keys PRIVATE_PATH PUBLIC_PATH"
            );
            return 2;
    }
}
catch (ArgumentException) when (args is ["identity", _])
{
    return 3;
}
catch (InvalidOperationException) when (args is ["identity", _])
{
    return 3;
}
catch (Exception error)
{
    // Do not echo PEM content, process environment, or parser exception messages.
    Console.Error.WriteLine($"Local development helper failed ({error.GetType().Name}).");
    return 1;
}
