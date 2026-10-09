using System;
using System.IO;
using SW.Serverless.Contract.Catalog;

// Reads the manifest at args[0] with the 10.0.2 parser and writes it back: what a released tool
// does to a manifest a newer one wrote. Prints the problems 10.0.2 finds, then the JSON.
var manifest = AdapterManifest.Parse(File.ReadAllText(args[0]));
foreach (var problem in manifest.Validate())
    Console.WriteLine("PROBLEM:" + problem);
Console.WriteLine("ENTRY:" + manifest.Entry);
Console.WriteLine("RUNTIME:" + manifest.Runtime);
Console.WriteLine("JSON:" + manifest.ToJson().ReplaceLineEndings(" "));
