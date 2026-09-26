using Paradise.Assets.Pipeline;
var dir = args[0];
Environment.SetEnvironmentVariable(KtxTool.PathEnvironmentVariable, args[1]);
foreach (var file in Directory.GetFiles(dir, "*.glb"))
{
    var result = GlbTextureWorkflows.ConvertEmbeddedTextures(file, dir);
    Console.WriteLine($"{Path.GetFileName(file)}: {result}");
}
