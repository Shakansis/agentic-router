namespace AgenticRouter.Api.Markdown;

public interface IMarkdownRenderer
{
  string RenderDocument(string markdown);

  string Render(
    string markdown
  );
}
