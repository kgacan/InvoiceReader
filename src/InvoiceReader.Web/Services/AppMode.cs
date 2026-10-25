namespace InvoiceReader.Web.Services;

/// <summary>
/// "Demo" (default): invented office, clients and invoices, a demo login and the sample-invoice
/// button. "Pilot": real offices with their own logins; nothing invented is ever added.
/// Set with the Mode setting (environment variable Mode=Pilot).
/// </summary>
public record AppMode(bool IsDemo)
{
    public static AppMode From(IConfiguration config)
    {
        var mode = config["Mode"]?.Trim();
        if (string.IsNullOrEmpty(mode) || mode.Equals("Demo", StringComparison.OrdinalIgnoreCase)) return new(true);
        if (mode.Equals("Pilot", StringComparison.OrdinalIgnoreCase)) return new(false);
        throw new InvalidOperationException($"Unknown Mode '{mode}'. Use Demo or Pilot.");
    }
}
