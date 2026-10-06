using Npgsql;

namespace TechAvail.Data;

public static class ConnectionStrings
{
    // DATABASE_URL is shared with the Python services, which take a postgresql:// URL; Npgsql
    // takes key=value pairs. Either form is accepted.
    public static string FromUrl(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.Ordinal) && !value.StartsWith("postgresql://", StringComparison.Ordinal))
            return value;
        var uri = new Uri(value);
        var user = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(user[0]),
            // No Kerberos here, and the aspnet image lacks libgssapi: probing for it logs an error.
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        if (user.Length > 1)
            builder.Password = Uri.UnescapeDataString(user[1]);
        return builder.ConnectionString;
    }
}
