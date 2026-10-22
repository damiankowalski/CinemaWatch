using System.Net;
using System.Text.Json;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Core;

/// <summary>User-facing descriptions of errors raised while talking to Cinema City (in the current language).</summary>
public static class ErrorMessages
{
    public static string Describe(Exception exception) => exception switch
    {
        CinemaCityException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } e => T(
            $"Cinema City odrzuciło zapytanie (HTTP {(int)e.StatusCode}). Możliwa blokada Cloudflare - spróbuj ponownie za kilka minut.",
            $"Cinema City rejected the request (HTTP {(int)e.StatusCode}). Possibly a Cloudflare block - try again in a few minutes."),
        CinemaCityException { StatusCode: HttpStatusCode.NotFound } => T(
            "Cinema City nie znalazło seansu (HTTP 404). Mógł zostać odwołany albo sprzedaż już się zakończyła.",
            "Cinema City could not find the screening (HTTP 404). It may have been cancelled or sales have ended."),
        CinemaCityException e => T(
            $"Cinema City zwróciło błąd HTTP {(int)e.StatusCode}.",
            $"Cinema City returned HTTP error {(int)e.StatusCode}."),
        TaskCanceledException or TimeoutException => T(
            "Przekroczono czas oczekiwania na odpowiedź Cinema City.",
            "Cinema City did not respond in time."),
        HttpRequestException e => T(
            $"Brak połączenia z Cinema City: {e.Message}",
            $"Cannot connect to Cinema City: {e.Message}"),
        JsonException => T(
            "Cinema City zwróciło dane w nieoczekiwanym formacie (możliwa zmiana API).",
            "Cinema City returned data in an unexpected format (the API may have changed)."),
        _ => exception.Message,
    };
}
