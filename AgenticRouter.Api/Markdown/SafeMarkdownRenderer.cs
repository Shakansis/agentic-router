using Ganss.Xss;
using Markdig;
using Markdown.ColorCode;

namespace AgenticRouter.Api.Markdown;

public sealed class SafeMarkdownRenderer : IMarkdownRenderer
{
  private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()
    .UseColorCode(
      HtmlFormatterType.Css
    )
    .DisableHtml()
    .Build();

  private readonly HtmlSanitizer _sanitizer;
  private readonly HtmlSanitizer _documentSanitizer;

  public SafeMarkdownRenderer()
  {
    _sanitizer = CreateSanitizer();
    _documentSanitizer = CreateSanitizer();
    foreach (var attribute in new[] { "id", "data-story-start", "data-story-end" })
      _documentSanitizer.AllowedAttributes.Add(attribute);
  }

  private static HtmlSanitizer CreateSanitizer()
  {
    var _sanitizer = new HtmlSanitizer();
    _sanitizer.AllowedTags.Clear();

    foreach (var tag in new[]
    {
      "a",
      "blockquote",
      "br",
      "code",
      "del",
      "div",
      "em",
      "h1",
      "h2",
      "h3",
      "h4",
      "h5",
      "h6",
      "hr",
      "li",
      "ol",
      "p",
      "pre",
      "span",
      "strong",
      "table",
      "tbody",
      "td",
      "th",
      "thead",
      "tr",
      "ul"
    })
    {
      _sanitizer.AllowedTags.Add(
        tag
      );
    }

    _sanitizer.AllowedAttributes.Clear();
    _sanitizer.AllowedAttributes.Add(
      "href"
    );
    _sanitizer.AllowedAttributes.Add(
      "class"
    );
    _sanitizer.AllowedAttributes.Add(
      "title"
    );
    _sanitizer.AllowedSchemes.Clear();
    _sanitizer.AllowedSchemes.Add(
      "http"
    );
    _sanitizer.AllowedSchemes.Add(
      "https"
    );
    _sanitizer.AllowedSchemes.Add(
      "mailto"
    );
    return _sanitizer;
  }

  public string RenderDocument(string markdown) => _documentSanitizer.Sanitize(StoryMarkdownDocument.Render(markdown));

  public string Render(
    string markdown
  )
  {
    var html = Markdig.Markdown.ToHtml(
      markdown,
      Pipeline
    );

    return _sanitizer.Sanitize(
      html
    );
  }
}
