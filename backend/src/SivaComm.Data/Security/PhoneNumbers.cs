namespace SivaComm.Data.Security;

/// <summary>
/// Ported from HMS_WEB's WhatsAppCloudMessageSender.TryNormalise, with the
/// same rule: a bare national number gets a country code only when one is
/// configured, because guessing would send someone's reset code to whoever in
/// another country holds those digits.
/// </summary>
public static class PhoneNumbers
{
    /// <summary>Digits only, with a country code: "919876543210".</summary>
    public static bool TryNormalise(string? phone, string defaultCountryCode, out string normalised)
    {
        normalised = new string((phone ?? "").Where(char.IsDigit).ToArray());

        // 00 is the other way of writing +.
        if (normalised.StartsWith("00")) normalised = normalised[2..];

        var cc = new string((defaultCountryCode ?? "").Where(char.IsDigit).ToArray());

        // Ten digits or fewer is a national number; anything longer already
        // carries its country code.
        if (normalised.Length <= 10 && cc.Length > 0 && !normalised.StartsWith(cc))
            normalised = cc + normalised;

        // Eleven or more: a country code plus a national number. HMS_WEB's
        // original allowed ten, which let a bare national number through
        // unprefixed when no default was configured — "9876543210" would
        // have gone to +98 (Iran) instead of being refused.
        return normalised.Length is >= 11 and <= 15;
    }

    /// <summary>"91••••••3210" — enough for an operator to tell two numbers
    /// apart, not enough to dial.</summary>
    public static string Mask(string normalised)
    {
        if (normalised.Length < 6) return new string('•', normalised.Length);
        return normalised[..2] + new string('•', normalised.Length - 6) + normalised[^4..];
    }
}
