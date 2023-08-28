using System.Linq;
using System.Net.Http;

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
    }
}