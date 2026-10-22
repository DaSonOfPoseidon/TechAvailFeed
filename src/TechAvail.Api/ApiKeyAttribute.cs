using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TechAvail.Api;

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
    // Every error is {"detail": "..."}.
    public static ObjectResult Detail(int status, string detail) =>
        new(new Dictionary<string, string> { ["detail"] = detail }) { StatusCode = status };
}
