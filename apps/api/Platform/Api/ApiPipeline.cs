using System.Text.Json;

namespace Ari.Platform.Api;

/// <summary>Throw from anywhere in an API route; the pipeline answers the one error shape.</summary>
public sealed class ApiException(int status, string message, string code) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Reads fields from a JSON body by type and length. Unknown shapes get field errors.
/// Used instead of Minimal API body binding so guards run first.
/// </summary>
public sealed class Validator
{
    private readonly JsonDocument _document;
    private readonly List<(string Field, string Problem)> _errors = new();

    public Validator(string json)
    {
        try { _document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ApiException(400, "The request body is not valid JSON.", "bad_json"); }
    }

    public bool HasErrors => _errors.Count > 0;

    public Validator Require(string field, out string value, int min = 1, int max = 5000)
    {
        value = "";
        if (_document.RootElement.ValueKind == JsonValueKind.Object
            && _document.RootElement.TryGetProperty(field, out var element))
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                _errors.Add((field, "must be text"));
                return this;
            }
            value = element.GetString() ?? "";
            if (value.Length < min) _errors.Add((field, $"must be at least {min} characters"));
            if (value.Length > max) _errors.Add((field, $"must be at most {max} characters"));
        }
        else _errors.Add((field, "is required"));
        return this;
    }

    public Validator Optional(string field, out string value, int max = 5000)
    {
        value = "";
        if (_document.RootElement.ValueKind == JsonValueKind.Object
            && _document.RootElement.TryGetProperty(field, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            if (value.Length > max) _errors.Add((field, $"must be at most {max} characters"));
        }
        return this;
    }

    public Validator RequireInt(string field, out long value, long min = long.MinValue, long max = long.MaxValue)
    {
        value = 0;
        if (_document.RootElement.ValueKind == JsonValueKind.Object
            && _document.RootElement.TryGetProperty(field, out var element))
        {
            if ((element.ValueKind is JsonValueKind.Number) && element.TryGetInt64(out var number))
            {
                value = number;
                if (value < min) _errors.Add((field, $"must be at least {min}"));
                if (value > max) _errors.Add((field, $"must be at most {max}"));
            }
            else _errors.Add((field, "must be a number"));
        }
        else _errors.Add((field, "is required"));
        return this;
    }

    /// <summary>Urls stored or shown as links: https and mailto only, no user info, no controls, no backslashes.</summary>
    public Validator Url(string field, ref string value, bool required)
    {
        if (value.Length == 0)
        {
            if (required) _errors.Add((field, "is required"));
            return this;
        }
        if (value.Contains('\\') || value.Any(char.IsControl)) { _errors.Add((field, "is not a valid URL")); return this; }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto || uri.Scheme == "tel")
            && string.IsNullOrEmpty(uri.UserInfo))
        {
            return this;
        }
        _errors.Add((field, "must be an https, mailto or tel URL"));
        return this;
    }

    public void Done()
    {
        if (_errors.Count == 0) return;
        var fields = _errors.Select(e => new { field = e.Field, problem = e.Problem });
        throw new ApiException(400, JsonSerializer.Serialize(fields), "invalid_fields");
    }
}
