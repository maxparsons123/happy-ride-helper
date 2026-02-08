namespace AdaSipClient.UI;

/// <summary>
/// Centralised colour and font definitions.
/// Change once here → updates entire UI.
/// </summary>
public static class Theme
{
    // ── Backgrounds ──
    public static readonly Color FormBg        = Color.FromArgb(24, 24, 28);
    public static readonly Color PanelBg       = Color.FromArgb(32, 33, 38);
    public static readonly Color InputBg       = Color.FromArgb(44, 45, 52);
    public static readonly Color LogBg         = Color.FromArgb(18, 18, 22);

    // ── Foregrounds ──
    public static readonly Color TextPrimary   = Color.FromArgb(230, 232, 240);
    public static readonly Color TextSecondary  = Color.FromArgb(150, 155, 168);
    public static readonly Color TextSuccess    = Color.FromArgb(74, 222, 128);
    public static readonly Color TextError      = Color.FromArgb(248, 113, 113);
    public static readonly Color TextWarning    = Color.FromArgb(250, 204, 21);

    // ── Accents ──
    public static readonly Color AccentBlue    = Color.FromArgb(96, 165, 250);
    public static readonly Color AccentGreen   = Color.FromArgb(34, 197, 94);
    public static readonly Color AccentRed     = Color.FromArgb(239, 68, 68);
    public static readonly Color AccentPurple  = Color.FromArgb(168, 85, 247);

    // ── Fonts ──
    public static readonly Font Body           = new("Segoe UI", 9.5F);
    public static readonly Font BodyBold       = new("Segoe UI", 9.5F, FontStyle.Bold);
    public static readonly Font Header         = new("Segoe UI Semibold", 11F);
    public static readonly Font Mono           = new("Cascadia Mono", 9F);
    public static readonly Font Small          = new("Segoe UI", 8F);

    // ── Helpers ──
    public static TextBox StyledInput(string placeholder = "")
    {
        return new TextBox
        {
            BackColor = InputBg,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Body,
            PlaceholderText = placeholder
        };
    }

    public static Button StyledButton(string text, Color bg)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = BodyBold,
            Cursor = Cursors.Hand,
            Height = 36
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    public static Label StyledLabel(string text, Color? color = null)
    {
        return new Label
        {
            Text = text,
            ForeColor = color ?? TextPrimary,
            Font = Body,
            AutoSize = true
        };
    }

    public static GroupBox StyledGroup(string text)
    {
        return new GroupBox
        {
            Text = text,
            ForeColor = TextSecondary,
            Font = Header,
            BackColor = PanelBg
        };
    }
}
