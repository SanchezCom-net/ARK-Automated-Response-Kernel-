using System.Net.Http;
using System.Text;
using ARK.UI.Core.Interfaces;

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

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        TryApplyContextInput<string>(nameof(Url),         v => Url         = v);
        TryApplyContextInput<string>(nameof(RequestBody), v => RequestBody = v);

        if (string.IsNullOrWhiteSpace(Url))
        {
            await logger.LogWarningAsync(Name, "[HTTP] URL не задан.").ConfigureAwait(false);
            return false;
        }

        HttpResponseMessage response = Method == HttpRequestMethod.POST
            ? await _httpClient
                .PostAsync(Url, new StringContent(RequestBody, Encoding.UTF8, "application/json"), cancellationToken)
                .ConfigureAwait(false)
            : await _httpClient.GetAsync(Url, cancellationToken).ConfigureAwait(false);

        string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        LastOutputValue = responseText;

        await logger.LogInfoAsync(Name,
            $"[HTTP] {Method} {Url} → {(int)response.StatusCode} ({responseText.Length} симв.)")
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode;
    }
}
