using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TechAvail.Api;

// When API_KEY is set, requests must send it in X-API-Key (compared in constant time).
public sealed class ApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<ApiSettings>().ApiKey;
        if (expected.Length == 0)
            return;
        var sent = context.HttpContext.Request.Headers["X-API-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected)))
            context.Result = Errors.Detail(401, "missing or wrong X-API-Key");
    }
}

public static class Errors
{
    // FastAPI's error body, so clients see the same shape.
    public static ObjectResult Detail(int status, string detail) =>
        new(new Dictionary<string, string> { ["detail"] = detail }) { StatusCode = status };
}
