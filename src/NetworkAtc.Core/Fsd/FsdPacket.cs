namespace NetworkAtc.Core.Fsd;

/// <summary>A raw FSD line split into its command prefix and colon-separated fields.</summary>
public sealed record FsdPacket(string Command, string[] Fields)
{
    public string this[int index] => index < Fields.Length ? Fields[index] : "";

    public static FsdPacket? Parse(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string command;
        string body;
        if (line[0] is '@' or '%')
        {
            command = line[..1];
            body = line[1..];
        }
        else if (line[0] is '#' or '$' && line.Length >= 3)
        {
            command = line[..3];
            body = line[3..];
        }
        else
        {
            return null;
        }
        return new FsdPacket(command, body.Split(':'));
    }

    public override string ToString() => Command + string.Join(':', Fields);
}
