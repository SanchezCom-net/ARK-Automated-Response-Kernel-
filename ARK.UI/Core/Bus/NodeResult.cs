namespace ARK.UI.Core.Bus;

public sealed record NodeResult
{
    public bool             IsSuccess     { get; init; }
    public DataBusPacket?   OutputPacket  { get; init; }
    public string?          ErrorMessage  { get; init; }

    public static NodeResult Success(DataBusPacket? packet = null)
        => new() { IsSuccess = true, OutputPacket = packet };

    public static NodeResult Failure(string message, DataBusPacket? packet = null)
        => new() { IsSuccess = false, ErrorMessage = message, OutputPacket = packet };
}
