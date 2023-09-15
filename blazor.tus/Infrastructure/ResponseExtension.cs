using System.Linq;
using System.Net.Http;
using blazor.tus.Execption;

namespace blazor.tus.Infrastructure
{
    public static class ResponseExtension
    {
        public static bool TryGetValueOfHeader(this HttpResponseMessage response, string key, out string? value)
        {
            value = null;
            if (!response.Headers.TryGetValues(key, out var values)) return false;
            value = values.First();
            return true;
        }

        public static string GetValueOfHeader(this HttpResponseMessage response, string key)
        {
            try
            {
                if (!response.Headers.TryGetValues(key.ToLower(), out var values)) throw new InvalidOperationException();
                return values.First();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidHeaderException($"Invalid {key} header");
            }
        }
    }
}