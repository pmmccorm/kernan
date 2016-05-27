using Grace.Execution;
using Grace.Parsing;
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

// A note about the handling of exceptions:
// Writing/reading from cache works on a "best effort" basis: if
// anything fails just ignore it and continue on, degrading into just
// reading the input file and translating.

namespace Grace
{
    public static class ObjectCache
    {
        private static DirectoryInfo dir = Directory.CreateDirectory("object_cache");
        private static IFormatter format = new BinaryFormatter();

        public static Node get(string filename)
        {
            string cacheFile = toCache(filename);
            if (isCurrent(filename))
            {
                Node n = deserialize(cacheFile);
                if (n == null)
                    return translate(filename);
                else
                    return n;
            }
            else
            {
                Node n = translate(filename);
                serialize(cacheFile, n);
                return n;
            }
        }

        private static string toCache(string filename)
        {
            return Path.Combine(dir.Name.ToString(), Path.GetFileName(filename) + ".cache");
        }

        private static bool isCurrent(string filename)
        {
            try
            {
                DateTime gracefile = File.GetLastWriteTime(filename);
                DateTime cachefile = File.GetLastWriteTime(toCache(filename));
                return gracefile < cachefile;
            }
            catch
            {
                return false;
            }
        }

        public static ParseNode parse(string filename)
        {
            using (StreamReader reader = File.OpenText(filename))
            {
                Parser parser = new Parser(
                        Path.GetFileNameWithoutExtension(filename),
                        reader.ReadToEnd());

                return parser.Parse();
            }
        }

        public static Node translate(string filename)
        {
            ParseNode module = parse(filename);

            ExecutionTreeTranslator ett = new ExecutionTreeTranslator();
            Node eModule = ett.Translate(module as ObjectParseNode);

            return eModule;
        }

        private static void serialize(string filepath, Node n)
        {
            Stream stream = null;
            try
            {
                stream = File.OpenWrite(filepath);
                format.Serialize(stream, n);
            }
            catch
            {
                stream.Dispose();
                try
                {
                    File.Delete(filepath);
                }
                catch { }
                return;
            }
        }

        private static Node deserialize(string filepath)
        {
            Stream stream = null;
            try
            {
                stream = File.OpenRead(filepath);
                return (Node)format.Deserialize(stream);
            }
            catch
            {
                stream.Dispose();
                try
                {
                    File.Delete(filepath);
                }
                catch { }
                return null;
            }
        }
    }
}
