using System;
using System.IO;
using Mono.Cecil;

internal static class Publicize
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3) throw new ArgumentException("Usage: Publicize <managed directory> <reference directory> <assembly filenames...>");
            string inputDirectory = Path.GetFullPath(args[0]);
            string outputDirectory = Path.GetFullPath(args[1]);
            if (!Directory.Exists(inputDirectory)) throw new DirectoryNotFoundException(inputDirectory);
            if (string.Equals(inputDirectory.TrimEnd(Path.DirectorySeparatorChar), outputDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Publicized references must be written outside the game managed directory.");
            Directory.CreateDirectory(outputDirectory);
            using (DefaultAssemblyResolver resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(inputDirectory);
                resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
                for (int i = 2; i < args.Length; i++)
                {
                    string filename = args[i];
                    if (Path.GetFileName(filename) != filename || !filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Expected an assembly filename without a directory: " + filename);
                    string source = Path.Combine(inputDirectory, filename);
                    string target = Path.Combine(outputDirectory, filename);
                    ReaderParameters reader = new ReaderParameters
                    {
                        AssemblyResolver = resolver, ReadSymbols = false, ReadingMode = ReadingMode.Immediate
                    };
                    using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(source, reader))
                    {
                        foreach (ModuleDefinition module in assembly.Modules)
                            foreach (TypeDefinition type in module.Types) Publish(type);
                        // Only a reference copy is changed. Identity, method flags and MVID are retained.
                        assembly.Write(target, new WriterParameters { WriteSymbols = false });
                    }
                    Console.WriteLine("Prepared reference: " + filename);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Publicizer failed: " + error.Message);
            return 1;
        }
    }

    private static void Publish(TypeDefinition type)
    {
        if (type.Name == "<Module>") return;
        bool nested = type.IsNested;
        type.Attributes = (type.Attributes & ~TypeAttributes.VisibilityMask) |
            (nested ? TypeAttributes.NestedPublic : TypeAttributes.Public);
        // Do not alter enum storage/special fields or their literal flags.
        if (!type.IsEnum)
            foreach (FieldDefinition field in type.Fields)
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
        foreach (MethodDefinition method in type.Methods)
        {
            // CLR static constructors retain their required private visibility.
            if (method.IsConstructor && method.IsStatic) continue;
            method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
        }
        foreach (TypeDefinition child in type.NestedTypes) Publish(child);
    }
}
