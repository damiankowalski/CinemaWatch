using System.Net;
using System.Text.Json;

namespace CinemaWatch.Core;

/// <summary>User-facing (Polish) descriptions of errors raised while talking to Cinema City.</summary>
public static class ErrorMessages
{
    public static string Describe(Exception exception) => exception switch
    {
        CinemaCityException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } e =>
            $"Cinema City odrzuciło zapytanie (HTTP {(int)e.StatusCode}). Możliwa blokada Cloudflare - spróbuj ponownie za kilka minut.",
        CinemaCityException { StatusCode: HttpStatusCode.NotFound } =>
            "Cinema City nie znalazło seansu (HTTP 404). Mógł zostać odwołany albo sprzedaż już się zakończyła.",
        CinemaCityException e =>
            $"Cinema City zwróciło błąd HTTP {(int)e.StatusCode}.",
        TaskCanceledException or TimeoutException =>
            "Przekroczono czas oczekiwania na odpowiedź Cinema City.",
        HttpRequestException e =>
            $"Brak połączenia z Cinema City: {e.Message}",
        JsonException =>
            "Cinema City zwróciło dane w nieoczekiwanym formacie (możliwa zmiana API).",
        _ => exception.Message,
    };
}
