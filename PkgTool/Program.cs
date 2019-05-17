﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;
using LibOrbisPkg.GP4;
using LibOrbisPkg.PFS;
using LibOrbisPkg.PKG;
using LibOrbisPkg.Util;

namespace PkgTool
{
  class Program
  {
    static void Main(string[] args)
    {
      if(!Verb.Run(Verbs, args, AppDomain.CurrentDomain.FriendlyName))
      {
        Console.WriteLine();
        Console.WriteLine("Use passcode \"fake\" to decrypt a FAKE PKG without knowing the actual passcode.");
      }
    }

    public static Verb[] Verbs = new[]
    {
      Verb.Create(
        "makepfs",
        ArgDef.List("input_project.gp4", "output_pfs.dat"),
        args =>
        {
          var proj = args[1];
          var output = args[2];
          var outFile = File.Create(output);
          var props = PfsProperties.MakeInnerPFSProps(PkgProperties.FromGp4(
              Gp4Project.ReadFrom(File.OpenRead(proj)),
              Path.GetDirectoryName(proj)));
          new PfsBuilder(props, Console.WriteLine).WriteImage(outFile);
        }),
      Verb.Create(
        "makeouterpfs",
        ArgDef.List(ArgDef.Switches("encrypt"), "input_project.gp4", "output_pfs.dat"),
        (switches, args) =>
        {
          var proj = args[1];
          var output = args[2];
          var outFile = File.Open(output, FileMode.Create);
          var project = Gp4Project.ReadFrom(File.OpenRead(proj));
          var projectDir = Path.GetDirectoryName(proj);
          var pkgProps = PkgProperties.FromGp4(project, projectDir);
          var EKPFS = Crypto.ComputeKeys(project.volume.Package.ContentId, project.volume.Package.Passcode, 1);
          Console.WriteLine("Preparing inner PFS...");
          var innerPfs = new PfsBuilder(PfsProperties.MakeInnerPFSProps(pkgProps), x => Console.WriteLine($"[innerpfs] {x}"));
          Console.WriteLine("Preparing outer PFS...");
          var outerPfs = new PfsBuilder(PfsProperties.MakeOuterPFSProps(pkgProps, innerPfs, EKPFS, switches["encrypt"]), x => Console.WriteLine($"[outerpfs] {x}"));
          outerPfs.WriteImage(outFile);
        }),
      Verb.Create(
        "makepkg",
        ArgDef.List("input_project.gp4", "output_directory"),
        args =>
        {
          var proj = args[1];
          var project = Gp4Project.ReadFrom(File.OpenRead(proj));
          var props = PkgProperties.FromGp4(project, Path.GetDirectoryName(proj));
          var outputPath = args[2];
          new PkgBuilder(props).Write(Path.Combine(
            outputPath,
            $"{project.volume.Package.ContentId}.pkg"));
        }),
      Verb.Create(
        "extractpkg",
        ArgDef.List("input.pkg", "passcode", "output_directory"),
        args =>
        {
          var pkgPath = args[1];
          var passcode = args[2];
          var outPath = args[3];
          Pkg pkg;

          var mmf = MemoryMappedFile.CreateFromFile(pkgPath);
          using (var s = mmf.CreateViewStream())
          {
            pkg = new PkgReader(s).ReadPkg();
          }
          var ekpfs = EkPfsFromPasscode(pkg, passcode);
          var outerPfsOffset = (long)pkg.Header.pfs_image_offset;
          using(var acc = mmf.CreateViewAccessor(outerPfsOffset, (long)pkg.Header.pfs_image_size))
          {
            var outerPfs = new PfsReader(acc, ekpfs);
            var inner = new PfsReader(new PFSCReader(outerPfs.GetFile("pfs_image.dat").GetView()));
            ExtractInParallel(inner, outPath);
          }
        }),
      Verb.Create(
        "extractpfs",
        ArgDef.List("input.dat", "output_directory"),
        args =>
        {
          var pfsPath = args[1];
          var outPath = args[2];
          using(var mmf = MemoryMappedFile.CreateFromFile(pfsPath))
          using(var acc = mmf.CreateViewAccessor())
          {
            var pfs = new PfsReader(acc);
            ExtractInParallel(pfs, outPath);
          }
        }),
      Verb.Create(
        "extractinnerpfs",
        ArgDef.List("input.pkg", "passcode", "output_pfs.dat"),
        args =>
        {
          var pkgPath = args[1];
          var passcode = args[2];
          var outPath = args[3];

          Pkg pkg;
          var mmf = MemoryMappedFile.CreateFromFile(pkgPath);
          using (var s = mmf.CreateViewStream())
          {
            pkg = new PkgReader(s).ReadPkg();
          }
          var ekpfs = EkPfsFromPasscode(pkg, passcode);
          var outerPfsOffset = (long)pkg.Header.pfs_image_offset;
          using(var acc = mmf.CreateViewAccessor(outerPfsOffset, (long)pkg.Header.pfs_image_size))
          {
            var outerPfs = new PfsReader(acc, ekpfs);
            var inner = outerPfs.GetFile("pfs_image.dat");
            using(var v = inner.GetView())
            using(var f = File.OpenWrite(outPath))
            {
              var buf = new byte[1024 * 1024];
              long wrote = 0;
              while(wrote < inner.size)
              {
                int toWrite = (int)Math.Min(inner.size - wrote, buf.Length);
                if(toWrite <= 0) break;
                v.Read(wrote, buf, 0, toWrite);
                f.Write(buf, 0, toWrite);
                wrote += toWrite;
              }
            }
          }
        }),
      Verb.Create(
        "extractouterpfs_e",
        ArgDef.List("input.pkg", "output_pfs_encrypted.dat"),
        args =>
        {
          var pkgPath = args[1];
          var outPath = args[2];
          Pkg pkg;
          using (var s = File.OpenRead(pkgPath))
          {
            pkg = new PkgReader(s).ReadPkg();
            var outer_pfs = new OffsetStream(s, (long)pkg.Header.pfs_image_offset);
            using (var o = File.Create(outPath))
            {
              outer_pfs.Position = 0;
              outer_pfs.CopyTo(o);
            }
          }
        }),
      Verb.Create(
        "extractouterpfs",
        ArgDef.List("input.pkg", "passcode", "pfs_image.dat"),
        args =>
        {
          var pkgPath = args[1];
          var passcode = args[2];
          var outPath = args[3];
          Pkg pkg;
          using (var s = File.OpenRead(pkgPath))
          {
            pkg = new PkgReader(s).ReadPkg();
            var outer_pfs = new OffsetStream(s, (long)pkg.Header.pfs_image_offset);
            var ekpfs = EkPfsFromPasscode(pkg, passcode);
            var pfs_seed = new byte[16];
            outer_pfs.Position = 0x370;
            outer_pfs.Read(pfs_seed, 0, 16);
            var outerpfs_size = outer_pfs.Length;
            var (tweakKey, dataKey) = Crypto.PfsGenEncKey(ekpfs, pfs_seed);
            s.Close();
            using (var pkgMM = MemoryMappedFile.CreateFromFile(pkgPath, FileMode.Open))
            using (var o = MemoryMappedFile.CreateFromFile(outPath, capacity: outerpfs_size, mapName: "output_outerpfs", mode: FileMode.Create))
            using (var outputView = o.CreateViewAccessor())
            using (var outerPfs = new MemoryMappedViewAccessor_(
                pkgMM.CreateViewAccessor((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size),
                true))
            {
              var transform = new XtsDecryptReader(outerPfs, dataKey, tweakKey);
              long length = (long)pkg.Header.pfs_image_size;
              const int copySize = 1024 * 1024;
              var buf = new byte[copySize];
              for(long i = 0; i < length; i += copySize)
              {
                int count = (int)Math.Min(copySize, length - i);
                transform.Read(i, buf, 0, count);
                outputView.WriteArray(i, buf, 0, count);
              }
              // Unset "encrypted" flag
              outputView.Write(0x1C, outputView.ReadByte(0x1C) & ~4);
            }
          }
        }),
      Verb.Create(
        "listentries",
        ArgDef.List("input.pkg"),
        args =>
        {
          var pkgPath = args[1];
          using(var file = File.OpenRead(pkgPath))
          {
            var pkg = new PkgReader(file).ReadPkg();
            Console.WriteLine("Offset\tSize\tFlags\t\t#\tName");
            var i = 0;
            foreach(var meta in pkg.Metas.Metas)
            {
              Console.WriteLine($"0x{meta.DataOffset:X2}\t0x{meta.DataSize:X}\t{meta.Flags1:X8}\t{i++}\t{meta.id}");
            }
          }
        }),
      Verb.Create(
        "extractentry",
        ArgDef.List("input.pkg", "entry_id", "output.bin"),
        args =>
        {
          var pkgPath = args[1];
          var idx = int.Parse(args[2]);
          var outPath = args[3];
          using (var pkgFile = File.OpenRead(pkgPath))
          {
            var pkg = new PkgReader(pkgFile).ReadPkg();
            if (idx < 0 || idx >= pkg.Metas.Metas.Count)
            {
              Console.WriteLine("Error: entry number out of range");
              return;
            }
            using (var outFile = File.Create(outPath))
            {
              var meta = pkg.Metas.Metas[idx];
              var entry = new SubStream(pkgFile, meta.DataOffset, meta.DataSize);
              outFile.SetLength(entry.Length);
              entry.CopyTo(outFile);
            }
          }
          return;
        }),
    };

    private static void ExtractInParallel(PfsReader inner, string outPath)
    {
      Console.WriteLine("Extracting in parallel...");
      Parallel.ForEach(
        inner.GetAllFiles(),
        () => new byte[0x10000],
        (f, _, buf) =>
        {
          var size = f.size;
          var pos = 0;
          var view = f.GetView();
          var path = Path.Combine(outPath, f.FullName.Replace('/', Path.DirectorySeparatorChar).Substring(1));
          var dir = path.Substring(0, path.LastIndexOf(Path.DirectorySeparatorChar));
          Directory.CreateDirectory(dir);
          using (var file = File.OpenWrite(path))
          {
            file.SetLength(size);
            while (size > 0)
            {
              var toRead = (int)Math.Min(size, buf.Length);
              view.Read(pos, buf, 0, toRead);
              file.Write(buf, 0, toRead);
              pos += toRead;
              size -= toRead;
            }
          }
          return buf;
        },
        x => { });
    }
    
    private static string SafeName(string name)
    {
      name = name.Replace("\\", "").Replace("/", "").Replace(":", "").Replace("*", "")
        .Replace("?", "").Replace("\"", "").Replace("<", "").Replace(">", "").Replace("|", "");
      if (name == ".." || name == ".")
      {
        name = "(" + name + ")";
      }
      return name;
    }

    private static byte[] EkPfsFromPasscode(Pkg pkg, string passcode)
    {
      if (passcode == "fake")
        return pkg.GetEkpfs();
      else
        return Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
    }
  }

  public class Verb
  {
    public string Name;
    public ArgDef[] Args;
    public Action<Dictionary<string, bool>, string[]> Action;
    public static Verb Create(string name, ArgDef[] args, Action<string[]> action)
    {
      return new Verb { Name = name, Args = args, Action = (ignore, a) => action(a) };
    }
    public static Verb Create(string name, ArgDef[] args, Action<Dictionary<string, bool>, string[]> action)
    {
      return new Verb { Name = name, Args = args, Action = action };
    }
    public override string ToString()
    {
      var options = Args.Select(x => x.Switch ? $"[--{x.Name}]" : $"<{x.Name}>").Aggregate((x, y) => $"{x} {y}");
      return Name + " " + options;
    }

    public static bool Run(Verb[] verbs, string[] args, string name)
    {
      if (args.Length > 0 && verbs.Where(x => x.Name == args[0]).FirstOrDefault() is Verb v)
      {
        var switches = new Dictionary<string, bool>();
        int numSwitches = 0;
        foreach(var x in v.Args.Where(x => x.Switch))
        {
          var s = args.Contains("--"+x.Name);
          if (s) numSwitches++;
          switches[x.Name] = s;
        }
        args = args.Skip(numSwitches).ToArray();
        if (args.Length == (v.Args.Where(a => !a.Switch).Count() + 1))
        {
          v.Action(switches, args);
        }
        else
        {
          Console.WriteLine($"Usage: {name} {v}");
        }
        return true;
      }
      Console.WriteLine($"Usage: {name} <verb> [options ...]");
      Console.WriteLine("");
      Console.WriteLine("Verbs:");
      foreach (var verb in verbs)
      {
        Console.WriteLine($"  {verb}");
      }
      return false;
    }
  }

  public class ArgDef
  {
    public string Name;
    public bool Switch = false;
    public static ArgDef[] List(ArgDef[] optionalArgs, params string[] names)
    {
      return optionalArgs.Concat(List(names)).ToArray();
    }
    public static ArgDef[] List(params string[] names)
    {
      var ret = new ArgDef[names.Length];
      for(var i = 0; i < ret.Length; i++)
      {
        ret[i] = new ArgDef { Name = names[i] };
      }
      return ret;
    }
    public static ArgDef[] Switches(params string[] names)
    {
      var ret = new ArgDef[names.Length];
      for (var i = 0; i < ret.Length; i++)
      {
        ret[i] = new ArgDef { Name = names[i], Switch = true };
      }
      return ret;
    }
  }
}
