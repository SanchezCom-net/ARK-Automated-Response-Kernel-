using System.Net.Http;
using System.Text;
using ARK.UI.Core.Bus;

namespace ARK.UI.Core.Nodes;

public enum HttpRequestMethod { GET, POST }

public sealed class Web_RequestNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(Url);

    private static readonly HttpClient _httpClient = new();

    public static IReadOnlyList<HttpRequestMethod> AllMethods { get; } =
        [HttpRequestMethod.GET, HttpRequestMethod.POST];

    private HttpRequestMethod _method = HttpRequestMethod.GET;
    private string _url         = string.Empty;
    private string _requestBody = string.Empty;

    public HttpRequestMethod Method
    {
        get => _method;
        set { if (_method != value) { _method = value; OnPropertyChanged(); } }
    }

    public string Url
    {
        get => _url;
        set { if (_url != value) { _url = value; OnPropertyChanged(); } }
    }

    public string RequestBody
    {
        get => _requestBody;
        set { if (_requestBody != value) { _requestBody = value; OnPropertyChanged(); } }
    }

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) Url = _s;
        }

        if (string.IsNullOrWhiteSpace(Url))
        {
            await NodeLogger!.LogWarningAsync(Name, "[HTTP] URL не задан.").ConfigureAwait(false);
            return NodeResult.Failure("URL не задан.");
        }

        HttpResponseMessage response = Method == HttpRequestMethod.POST
            ? await _httpClient
                .PostAsync(Url, new StringContent(RequestBody, Encoding.UTF8, "application/json"), ct)
                .ConfigureAwait(false)
            : await _httpClient.GetAsync(Url, ct).ConfigureAwait(false);

        string responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        LastOutputValue = responseText;

        await NodeLogger!.LogInfoAsync(Name,
            $"[HTTP] {Method} {Url} → {(int)response.StatusCode} ({responseText.Length} симв.)")
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return NodeResult.Failure($"HTTP {(int)response.StatusCode}");

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, responseText);
        return NodeResult.Success(_out);
    }
}
