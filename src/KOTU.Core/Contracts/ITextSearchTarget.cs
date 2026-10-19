namespace KOTU.Core.Contracts;

public enum TextSearchCommand { Find, Replace, Next, Previous, ReturnToDocument, EscapeReleased }

/// <summary>Routes document search keys across a detached bottom bar and a composite content host.</summary>
public interface ITextSearchTarget
{
    bool TryTextSearchCommand(TextSearchCommand command, bool fromEnter = false, bool repeat = false);
}
