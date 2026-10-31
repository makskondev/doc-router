namespace DocRouter.Core;

public sealed record RouteStepDef(string Name, Type StepType, Action<IStep>? Configure);

public sealed class RouteDefinition(string docType, IReadOnlyList<RouteStepDef> steps)
{
    public string DocType { get; } = docType;
    public IReadOnlyList<RouteStepDef> Steps { get; } = steps;

    public int IndexOf(string stepName)
    {
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i].Name == stepName) return i;
        return -1;
    }
}

public sealed class RouteRegistry(IReadOnlyDictionary<string, RouteDefinition> routes)
{
    public bool TryGet(string docType, out RouteDefinition route) => routes.TryGetValue(docType, out route!);
    public RouteDefinition Get(string docType) => routes[docType];
    public IEnumerable<string> DocTypes => routes.Keys;
}

public sealed class StepListBuilder
{
    private readonly RoutesBuilder _root;
    private readonly List<RouteStepDef> _steps = new();

    internal StepListBuilder(RoutesBuilder root) => _root = root;
    internal IReadOnlyList<RouteStepDef> Steps => _steps;

    public StepListBuilder Step<T>(Action<T>? configure = null) where T : IStep
    {
        Add(typeof(T), configure is null ? null : s => configure((T)s));
        return this;
    }

    public StepListBuilder UseTemplate(string template)
    {
        foreach (var d in _root.GetTemplate(template)) Add(d.StepType, d.Configure);
        return this;
    }

    // Имя шага уникально в пределах маршрута: FindFile, FindFile#2...
    private void Add(Type type, Action<IStep>? configure)
    {
        var baseName = type.Name;
        var n = _steps.Count(s => s.Name == baseName || s.Name.StartsWith(baseName + "#"));
        _steps.Add(new RouteStepDef(n == 0 ? baseName : $"{baseName}#{n + 1}", type, configure));
    }
}

public sealed class RoutesBuilder
{
    private readonly Dictionary<string, StepListBuilder> _templates = new();
    private readonly Dictionary<string, StepListBuilder> _routes = new();

    public RoutesBuilder Template(string name, Action<StepListBuilder> build)
    {
        var b = new StepListBuilder(this);
        build(b);
        _templates[name] = b;
        return this;
    }

    /// <summary>Маршрут для типа документа (RouteKey = тип).</summary>
    public StepListBuilder For(string docType)
    {
        if (_routes.ContainsKey(docType)) throw new InvalidOperationException($"Маршрут для типа '{docType}' уже определён");
        var b = new StepListBuilder(this);
        _routes[docType] = b;
        return b;
    }

    internal IReadOnlyList<RouteStepDef> GetTemplate(string name) =>
        _templates.TryGetValue(name, out var t) ? t.Steps : throw new InvalidOperationException($"Шаблон '{name}' не найден");

    public RouteRegistry Build()
    {
        var result = new Dictionary<string, RouteDefinition>();
        foreach (var (type, b) in _routes)
        {
            if (b.Steps.Count == 0) throw new InvalidOperationException($"Маршрут '{type}' не содержит шагов");
            result[type] = new RouteDefinition(type, b.Steps.ToList());
        }
        return new RouteRegistry(result);
    }
}
