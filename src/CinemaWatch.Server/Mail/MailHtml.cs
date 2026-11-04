using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace CinemaWatch.Server.Mail;

/// <summary>The HTML part of an e-mail: plain paragraphs and links, everything encoded.</summary>
internal sealed class MailHtml
{
    // Keeps Polish letters readable in the HTML part instead of turning them into numeric entities.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private readonly StringBuilder _html = new();

    public static string Encode(string text) => Encoder.Encode(text);

    /// <summary>&lt;a href="…"&gt;text&lt;/a&gt;</summary>
    public static string Link(string href, string text) => $"<a href=\"{Encoder.Encode(href)}\">{Encoder.Encode(text)}</a>";

    public MailHtml Paragraph(string text)
    {
        _html.Append("<p>").Append(Encoder.Encode(text)).Append("</p>");
        return this;
    }

    /// <summary>A paragraph whose lines are separated by &lt;br&gt;; each line is either text or ready HTML.</summary>
    public MailHtml Lines(params (string Text, bool IsHtml)[] lines)
    {
        _html.Append("<p>").AppendJoin("<br>", lines.Select(l => l.IsHtml ? l.Text : Encoder.Encode(l.Text))).Append("</p>");
        return this;
    }

    public MailHtml Raw(string html)
    {
        _html.Append(html);
        return this;
    }

    public override string ToString() => _html.ToString();
}
