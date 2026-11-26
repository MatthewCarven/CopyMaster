using System.Globalization;
using System.Text;

namespace CopyMaster.Engine.Jobs;

/// <summary>
/// The file the shell DLL writes to hand a job to CopyMaster.exe: UTF-8 lines of
/// <c>key=value</c> after a header. Windows file names can't contain line breaks, so no
/// escaping is needed, and the C++ side stays trivial. Values may contain '=' (only the
/// first one splits). Unknown keys are ignored, so later versions can add some.
/// <code>
/// CopyMaster job 1
/// op=copy
/// dest=C:\Users\Admin\Desktop
/// clipseq=1234
/// src=C:\Temp\a.txt
/// src=C:\Temp\Some folder
/// </code>
/// </summary>
public static class JobFile
{
    public const string Header = "CopyMaster job 1";

    public static void Write(string path, JobSpec spec) =>
        File.WriteAllText(path, Format(spec), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static string Format(JobSpec spec)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append("op=").Append(spec.Operation == CopyOperation.Move ? "move" : "copy").Append('\n');
        text.Append("dest=").Append(spec.Destination).Append('\n');
        if (spec.ClipboardSequence is uint sequence)
        {
            text.Append("clipseq=").Append(sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        foreach (string source in spec.Sources) text.Append("src=").Append(source).Append('\n');
        return text.ToString();
    }

    /// <exception cref="FormatException">It isn't a CopyMaster job file, or it's missing something.</exception>
    public static JobSpec Read(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static JobSpec Parse(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0] != Header) throw new FormatException("This isn't a CopyMaster job file.");

        CopyOperation? operation = null;
        string? destination = null;
        uint? sequence = null;
        var sources = new List<string>();
        foreach (string line in lines.Skip(1))
        {
            int split = line.IndexOf('=');
            if (split <= 0) throw new FormatException($"Unreadable line in the job file: {line}");
            string key = line[..split];
            string value = line[(split + 1)..];
            switch (key)
            {
                case "op":
                    operation = value switch
                    {
                        "copy" => CopyOperation.Copy,
                        "move" => CopyOperation.Move,
                        _ => throw new FormatException($"Unknown operation in the job file: {value}"),
                    };
                    break;
                case "dest":
                    destination = value;
                    break;
                case "clipseq":
                    sequence = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "src":
                    sources.Add(value);
                    break;
            }
        }

        if (operation is null) throw new FormatException("The job file doesn't say whether to copy or move.");
        if (string.IsNullOrEmpty(destination)) throw new FormatException("The job file has no destination.");
        if (sources.Count == 0) throw new FormatException("The job file has nothing to copy.");
        return new JobSpec(operation.Value, destination, sources) { ClipboardSequence = sequence };
    }
}
