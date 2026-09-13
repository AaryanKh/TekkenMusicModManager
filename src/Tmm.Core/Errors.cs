namespace Tmm.Core;

/// <summary>Base for all application errors. The UI catches this and shows a dialog.</summary>
public class TmmException : Exception
{
    public TmmException(string message) : base(message) { }
    public TmmException(string message, Exception inner) : base(message, inner) { }
}

public sealed class GameNotFoundException : TmmException { public GameNotFoundException(string m) : base(m) { } }
public sealed class CatalogNotBuiltException : TmmException { public CatalogNotBuiltException(string m) : base(m) { } }
public sealed class ExtractionException : TmmException { public ExtractionException(string m) : base(m) { } }
public sealed class DecodeException : TmmException
{
    public DecodeException(string m) : base(m) { }
    public DecodeException(string m, Exception inner) : base(m, inner) { }
}
public sealed class AnalysisException : TmmException { public AnalysisException(string m) : base(m) { } }
public sealed class NoFitFoundException : TmmException { public NoFitFoundException(string m) : base(m) { } }
public class RenderException : TmmException { public RenderException(string m) : base(m) { } }

/// <summary>Raised when a rendered buffer is not sample-exact. This is a bug, never a warning.</summary>
public sealed class LengthMismatchException : RenderException { public LengthMismatchException(string m) : base(m) { } }

public sealed class WemFormatException : TmmException { public WemFormatException(string m) : base(m) { } }
public sealed class PackException : TmmException
{
    public PackException(string m) : base(m) { }
    public PackException(string m, Exception inner) : base(m, inner) { }
}
public class InstallException : TmmException { public InstallException(string m) : base(m) { } }
public sealed class SlotConflictException : InstallException
{
    public IReadOnlyList<Mods.Conflict> Conflicts { get; }
    public SlotConflictException(string m, IReadOnlyList<Mods.Conflict> conflicts) : base(m) => Conflicts = conflicts;
}
