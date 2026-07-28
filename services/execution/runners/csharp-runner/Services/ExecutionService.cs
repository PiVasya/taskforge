using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Runner.Services;

public sealed class ExecutionService : IExecutionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class ExecRequest
    {
        public string AssemblyBase64 { get; set; } = "";
        public string? PdbBase64 { get; set; }
        public string? Input { get; set; }
    }

    private sealed class ExecResponse
    {
        public bool Ok { get; set; }
        public string Stdout { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public (bool Ok, string Stdout, string Error) Run(byte[] pe, byte[] pdb, string input, TimeSpan timeout)
    {
        // если вход пустой – отправляем хотя бы перевод строки (важно для Console.ReadLine)
        var normalizedInput = string.IsNullOrEmpty(input) ? "\n" : input;

        var entryDll = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryDll))
            return (false, "", "Runner entry assembly location not found.");

        var req = new ExecRequest
        {
            AssemblyBase64 = Convert.ToBase64String(pe),
            PdbBase64 = (pdb != null && pdb.Length > 0) ? Convert.ToBase64String(pdb) : null,
            Input = normalizedInput
        };

        var json = JsonSerializer.Serialize(req, JsonOpts);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{entryDll}\" --exec",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var globalizationInvariant = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        var language = Environment.GetEnvironmentVariable("LANG");
        var locale = Environment.GetEnvironmentVariable("LC_ALL");
        var timezone = Environment.GetEnvironmentVariable("TZ");
        psi.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(inheritedPath)) psi.Environment["PATH"] = inheritedPath;
        if (!string.IsNullOrWhiteSpace(dotnetRoot)) psi.Environment["DOTNET_ROOT"] = dotnetRoot;
        if (!string.IsNullOrWhiteSpace(globalizationInvariant)) psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = globalizationInvariant;
        if (!string.IsNullOrWhiteSpace(language)) psi.Environment["LANG"] = language;
        if (!string.IsNullOrWhiteSpace(locale)) psi.Environment["LC_ALL"] = locale;
        if (!string.IsNullOrWhiteSpace(timezone)) psi.Environment["TZ"] = timezone;
        psi.Environment["HOME"] = "/tmp";
        psi.Environment["TMPDIR"] = "/tmp";
        psi.Environment["TASKFORGE_SUBMISSION"] = "1";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = false };

        try
        {
            p.Start();

            // Отправляем request в stdin и закрываем, чтобы child понял “ввода больше нет”
            p.StandardInput.Write(json);
            p.StandardInput.Close();

            // Ждём завершения по таймауту
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(1000); } catch { }
                return (false, "", "Time limit exceeded.");
            }

            var childStdout = p.StandardOutput.ReadToEnd();
            var childStderr = p.StandardError.ReadToEnd();

            if (string.IsNullOrWhiteSpace(childStdout))
            {
                // если внезапно ничего не пришло — показываем stderr, если есть
                var err = string.IsNullOrWhiteSpace(childStderr) ? "Execution failed." : childStderr;
                return (false, "", err);
            }

            ExecResponse? resp = null;
            try
            {
                resp = JsonSerializer.Deserialize<ExecResponse>(childStdout, JsonOpts);
            }
            catch
            {
                // если child “сломался” и вернул не JSON
                var err = "Bad exec response.\n" + childStdout;
                if (!string.IsNullOrWhiteSpace(childStderr))
                    err += "\n" + childStderr;
                return (false, "", err);
            }

            if (resp == null)
                return (false, "", "Empty exec response.");

            return resp.Ok
                ? (true, resp.Stdout ?? "", "")
                : (false, resp.Stdout ?? "", resp.Error ?? "Execution error.");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}
