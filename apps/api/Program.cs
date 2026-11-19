using Ari.Platform;

var env = Env.Load();

// CLI modes are discovered the same way features are; feature areas own their commands.
var mode = typeof(ProgramMarker).Assembly.GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && typeof(ICliMode).IsAssignableFrom(t))
    .Select(t => (ICliMode)Activator.CreateInstance(t)!)
    .FirstOrDefault(m => m.Name == args.FirstOrDefault());

if (mode is not null)
{
    return await mode.Run(env, args);
}

var features = typeof(ProgramMarker).Assembly.GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && typeof(IFeature).IsAssignableFrom(t))
    .Select(t => (IFeature)Activator.CreateInstance(t)!)
    .OrderBy(f => f.Order)
    .ToList();

var app = ApiHost.Build(env,
    configure: services =>
    {
        foreach (var feature in features) feature.Register(services);
    },
    map: mapped =>
    {
        foreach (var feature in features) feature.Map(mapped);
    });
app.Run();
return 0;
