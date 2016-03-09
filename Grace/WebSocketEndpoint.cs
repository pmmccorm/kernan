using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Collections.Concurrent;
using System.Xml;
using System.Xml.XPath;
using System.Xml.Linq;
using Grace.Execution;
using Grace.Parsing;
using Grace.Runtime;
using Grace.Utility;

namespace Grace
{
    internal class WebSocketEndpoint
    {
        private static Dictionary<string, string> moduleCode
            = new Dictionary<string, string>();

        private static Dictionary<int, EventWaitHandle> eventWaitHandlers
            = new Dictionary<int, EventWaitHandle>();

        private static Dictionary<int, GraceObject> responses
            = new Dictionary<int, GraceObject>();

        public static void AddEventHandle(int key, EventWaitHandle handle)
        {
            eventWaitHandlers[key] = handle;
        }

        public static GraceObject GetEventResult(int key)
        {
            var ret = responses[key];
            responses.Remove(key);
            eventWaitHandlers.Remove(key);
            return ret;
        }

        private static bool loadCachedModule(string path,
            Interpreter interp)
        {
            if (moduleCode.ContainsKey(path))
            {
                interp.LoadModuleString(path, moduleCode[path]);
                return true;
            }
            return false;
        }

        private static int runModule(string code, string modname,
                string mode, WSOutputSink sink)
        {
            var interp = new Interpreter(sink);
            ErrorReporting.SetSink(new OutputSinkWrapper(Console.Error));
            interp.AddModuleRoot(Path.GetFullPath("."));
            interp.FailedImportHook = loadCachedModule;
            interp.LoadPrelude();
            Parser parser = new Parser(modname, code);
            ParseNode module;
            try
            {
                module = parser.Parse();
                ExecutionTreeTranslator ett = new ExecutionTreeTranslator();
                Node eModule = ett.Translate(module as ObjectParseNode);
                sink.SendEvent("build-succeeded", modname);
                if (mode == "build")
                    return 0;
                interp.EnterModule(modname);
                try
                {
                    eModule.Evaluate(interp);
                }
                catch (GraceExceptionPacketException ex)
                {
                    sink.SendRuntimeError(ex.ExceptionPacket.Description,
                            ex.ExceptionPacket.StackTrace);
                }
            }
            catch (StaticErrorException ex)
            {
                sink.SendStaticError(ex.Code, ex.Module, ex.Line,
                        ex.Message);
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (WebSocketClosedException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine(
                        "An internal error occurred. "
                        + "Debugging information follows.");
                System.Console.Error.WriteLine("Runtime version: "
                        + Interpreter.GetRuntimeVersion());
                System.Console.Error.WriteLine(ex);
                System.Console.Error.WriteLine(ex.StackTrace);
                System.Console.Error.WriteLine(
                        "\nAn internal error occurred. "
                        + "This is a bug in the implementation.");
            }
            sink.SendEvent("execution-complete", modname);
            return 0;
        }

        private static GraceObject objectFromElement(XElement root)
        {
            GraceObject obj = GraceObject.Done;
            var stringEl = root.XPathSelectElement("//string");
            if (stringEl != null)
            {
                obj = GraceString.Create(stringEl.Value);
            }
            var numberEl = root.XPathSelectElement("//number");
            if (numberEl != null)
            {
                double d;
                if (double.TryParse(numberEl.Value, out d))
                    obj = GraceNumber.Create(d);
            }
            var objectEl = root.XPathSelectElement("//object");
            if (objectEl != null)
            {
                int objKey;
                if (int.TryParse(objectEl.Value, out objKey))
                {
                    obj = new GraceForeignObject(objKey);
                }
            }
            return obj;
        }

        private static void processResponse(XElement root)
        {
            var keyEl = root.XPathSelectElement("//key");
            if (keyEl == null)
                return;
            int key;
            int.TryParse(keyEl.Value, out key);
            GraceObject obj = objectFromElement(root);
            responses[key] = obj;
            eventWaitHandlers[key].Set();
        }

        private static void processCallback(XElement root,
                WSOutputSink sink)
        {
            var blockEl = root.XPathSelectElement("//block");
            if (blockEl == null)
                return;
            int blockID;
            int.TryParse(blockEl.Value, out blockID);
            var argsEl = root.XPathSelectElement("//args");
            var argsEnum = argsEl.XPathSelectElements("//item");
            var args = new object[argsEnum.Count()];
            int i = 0;
            foreach (var arg in argsEnum)
            {
                args[i] = objectFromElement(arg);
            }
            sink.ReceiveCallback(blockID, args);
        }

        public static int WSServe()
        {
            var ws = new Grace.Execution.WebSocketServer();
            var wss = ws.Start();
            Thread runningThread = null;
            WSOutputSink runningSink = null;
            while (true)
            {
                wss.JsonReceived += (o, e) => {
                    var je = (JsonWSEvent)e;
                    var root = je.Root;
                    var md = root.XPathSelectElement("//mode");
                    var mode = md.Value;
                    if (mode == "stop")
                    {
                        if (runningThread != null)
                        {
                            runningSink.Stop();
                            runningThread.Abort();
                            runningThread = null;
                            runningSink = null;
                        }
                        return;
                    }
                    else if (mode == "response")
                    {
                        processResponse(root);
                        return;
                    }
                    else if (mode == "callback")
                    {
                        if (runningThread != null)
                        {
                            processCallback(root, runningSink);
                        }
                        return;
                    }
                    var cn = root.XPathSelectElement("//code");
                    var mn = root.XPathSelectElement("//modulename");
                    var code = cn.Value;
                    var modname = mn.Value;
                    moduleCode[modname] = code;
                    var sink = new WSOutputSink(wss);
                    runningSink = sink;
                    var thread = new Thread(() => {
                            var startSent = wss.SentFrames;
                            var startReceived = wss.ReceivedFrames;
                            var startTime = DateTime.Now;
                            try
                            {
                                runModule(code, modname, mode, sink);
                                log("Module " + modname
                                        + " completed " + mode + ".");
                                runningThread = null;
                                summarise(wss, startSent, startReceived,
                                        DateTime.Now - startTime);
                            }
                            catch (WebSocketClosedException)
                            {
                                log("Lost WebSocket connection "
                                        + "while running module " + modname
                                        + ".");
                                runningThread = null;
                                summarise(wss, startSent, startReceived,
                                        DateTime.Now - startTime);
                            }
                            catch (ThreadAbortException)
                            {
                                log("Execution of " + modname
                                        + " aborted.");
                                Thread.ResetAbort();
                                summarise(wss, startSent, startReceived,
                                        DateTime.Now - startTime);
                            }
                    });
                    runningThread = thread;
                    log("Spawning thread to " + mode + " "
                            + modname + "...");
                    thread.Start();
                };
                wss.Run();
                wss = ws.Next();
            }
        }

        private static void summarise(WebSocketStream wss,
                int ss, int sr, TimeSpan ts)
        {
            Console.WriteLine("                      "
                        + "Duration:        {0:h\\:mm\\:ss\\.fff}",
                    ts);
            Console.WriteLine("                      Sent frames:     {0}",
                    wss.SentFrames - ss);
            Console.WriteLine("                      Received frames: {0}",
                    wss.ReceivedFrames - sr);
        }

        private static void log(string message)
        {
            Console.WriteLine("[{0:yyMMdd HHmmss.fff}] {1}",
                    DateTime.Now, message);
        }

        private static int wsrepl(string filename)
        {
            var ws = new Grace.Execution.WebSocketServer();
            var wss = ws.Start();
            var ls = new LocalScope("repl-inner");
            var obj = new UserObject();
            var interp = REPL.CreateInterpreter(obj, ls,
                    new WSOutputSink(wss));
            interp.LoadPrelude();
            var dir = Path.GetFullPath(".");
            interp.AddModuleRoot(dir);
            ErrorReporting.SilenceError("P1001");
            var memo = interp.Memorise();
            string accum = String.Empty;
            bool unfinished;
            GraceObject result;
            wss.JsonReceived += (o, e) => {
                var je = (JsonWSEvent)e;
                var root = je.Root;
                var cn = root.XPathSelectElement("//code");
                if (cn == null)
                    return;
                var line = cn.Value;
                //var line = ((TextWSEvent)e).Text;
                Console.WriteLine("got text: " + line);
                accum += line.Replace("\u0000", "") + "\n";
                var r = REPL.RunLine(
                        interp, obj, memo, accum, out unfinished,
                        out result);
                if (result != null)
                    ls["LAST"] = result;
                if (unfinished)
                {
                    // "Unexpected end of file" is expected here
                    // for unfinished statements.
                    unfinished = false;
                }
                else if (r != 0)
                {
                    // All other errors are errors, and should
                    // clear the accumulated buffer and let the
                    // user start again.
                    accum = String.Empty;
                }
                else
                {
                    accum = String.Empty;
                }
            };
            wss.Run();
            return 0;
        }

    }

    class Callback
    {
        public GraceObject block;
        public object[] args;

        public Callback(GraceObject _block, object[] _args)
        {
            block = _block;
            args = _args;
        }
    }

    public class WSOutputSink : OutputSink, RPCSink
    {
        private WebSocketStream wss;
        private volatile bool stop;

        private BlockingCollection<Callback> callbacks
            = new BlockingCollection<Callback>();

        public WSOutputSink(WebSocketStream _wss)
        {
            wss = _wss;
        }

        public void Stop()
        {
            stop = true;
        }

        public void WriteLine(string s)
        {
            if (stop)
                return;
            var stream = new MemoryStream(10240);
            var jsonWriter = JsonReaderWriterFactory.CreateJsonWriter(
                    stream
                );
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("root");
            var output = xmlDoc.CreateElement("output");
            var tn = xmlDoc.CreateTextNode(s);
            output.AppendChild(tn);
            root.AppendChild(output);
            var mode = xmlDoc.CreateElement("mode");
            tn = xmlDoc.CreateTextNode("output");
            mode.AppendChild(tn);
            root.AppendChild(mode);
            root.SetAttribute("type", "object");
            xmlDoc.AppendChild(root);
#if DEBUG_WS
            Console.WriteLine(xmlDoc.OuterXml);
#endif
            xmlDoc.WriteTo(jsonWriter);
            jsonWriter.Flush();
#if DEBUG_WS
            Console.WriteLine(
                "Capacity = {0}, Length = {1}, Position = {2}\n",
                stream.Capacity.ToString(),
                stream.Length.ToString(),
                stream.Position.ToString());
#endif
            stream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(stream);
            wss.Send(reader.ReadToEnd());
        }

        public void SendRuntimeError(string message,
                IList<string> stackTrace)
        {
            if (stop)
                return;
            var stream = new MemoryStream(10240);
            var jsonWriter = JsonReaderWriterFactory.CreateJsonWriter(
                    stream
                );
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("root");
            // output = message
            var output = xmlDoc.CreateElement("output");
            var tn = xmlDoc.CreateTextNode(message);
            output.AppendChild(tn);
            root.AppendChild(output);
            // mode = "runtime-error"
            var mode = xmlDoc.CreateElement("mode");
            tn = xmlDoc.CreateTextNode("runtime-error");
            mode.AppendChild(tn);
            root.AppendChild(mode);
            // stack = [ line1, line2, ... ]
            var stack = xmlDoc.CreateElement("stack");
            stack.SetAttribute("type", "array");
            foreach (var l in stackTrace)
            {
                var el = xmlDoc.CreateElement("item");
                tn = xmlDoc.CreateTextNode(l);
                el.AppendChild(tn);
                stack.AppendChild(el);
            }
            root.AppendChild(stack);

            root.SetAttribute("type", "object");
            xmlDoc.AppendChild(root);
#if DEBUG_WS
            Console.WriteLine(xmlDoc.OuterXml);
#endif
            xmlDoc.WriteTo(jsonWriter);
            jsonWriter.Flush();
#if DEBUG_WS
            Console.WriteLine(
                "Capacity = {0}, Length = {1}, Position = {2}\n",
                stream.Capacity.ToString(),
                stream.Length.ToString(),
                stream.Position.ToString());
#endif
            stream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(stream);
            wss.Send(reader.ReadToEnd());
        }

        public void SendStaticError(string code,
                string module, int line, string message)
        {
            if (stop)
                return;
            var stream = new MemoryStream(10240);
            var jsonWriter = JsonReaderWriterFactory.CreateJsonWriter(
                    stream
                );
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("root");
            // msg = message
            var msg = xmlDoc.CreateElement("message");
            var tn = xmlDoc.CreateTextNode(message);
            msg.AppendChild(tn);
            root.AppendChild(msg);
            // mode = "static-error"
            var mode = xmlDoc.CreateElement("mode");
            tn = xmlDoc.CreateTextNode("static-error");
            mode.AppendChild(tn);
            root.AppendChild(mode);
            // line = line
            var lineEl = xmlDoc.CreateElement("line");
            lineEl.SetAttribute("type", "number");
            tn = xmlDoc.CreateTextNode("" + line);
            lineEl.AppendChild(tn);
            root.AppendChild(lineEl);
            // module = module
            var moduleEl = xmlDoc.CreateElement("module");
            tn = xmlDoc.CreateTextNode(module);
            moduleEl.AppendChild(tn);
            root.AppendChild(moduleEl);
            // code = code
            var codeEl = xmlDoc.CreateElement("code");
            tn = xmlDoc.CreateTextNode(code);
            codeEl.AppendChild(tn);
            root.AppendChild(codeEl);

            root.SetAttribute("type", "object");
            xmlDoc.AppendChild(root);
#if DEBUG_WS
            Console.WriteLine(xmlDoc.OuterXml);
#endif
            xmlDoc.WriteTo(jsonWriter);
            jsonWriter.Flush();
#if DEBUG_WS
            Console.WriteLine(
                "Capacity = {0}, Length = {1}, Position = {2}\n",
                stream.Capacity.ToString(),
                stream.Length.ToString(),
                stream.Position.ToString());
#endif
            stream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(stream);
            wss.Send(reader.ReadToEnd());
        }

        public void SendEvent(string eventName, string key)
        {
            if (stop)
                return;
            var stream = new MemoryStream(10240);
            var jsonWriter = JsonReaderWriterFactory.CreateJsonWriter(
                    stream
                );
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("root");
            var output = xmlDoc.CreateElement("event");
            var tn = xmlDoc.CreateTextNode(eventName);
            output.AppendChild(tn);
            root.AppendChild(output);
            output = xmlDoc.CreateElement("key");
            tn = xmlDoc.CreateTextNode(key);
            output.AppendChild(tn);
            root.AppendChild(output);
            root.SetAttribute("type", "object");
            xmlDoc.AppendChild(root);
#if DEBUG_WS
            Console.WriteLine(xmlDoc.OuterXml);
#endif
            xmlDoc.WriteTo(jsonWriter);
            jsonWriter.Flush();
#if DEBUG_WS
            Console.WriteLine(
                "Capacity = {0}, Length = {1}, Position = {2}\n",
                stream.Capacity.ToString(),
                stream.Length.ToString(),
                stream.Position.ToString());
#endif
            stream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(stream);
            wss.Send(reader.ReadToEnd());
        }

        private static int callKey;

        private static List<GraceBlock> blocks
            = new List<GraceBlock>();

        private static Dictionary<GraceBlock, int> blockMap
            = new Dictionary<GraceBlock, int>();

        private static int getBlockId(GraceBlock b)
        {
            if (blockMap.ContainsKey(b))
                return blockMap[b];
            blocks.Add(b);
            blockMap[b] = blocks.Count - 1;
            return blocks.Count - 1;
        }

        public GraceObject SendCall(int receiver, string name,
                object[] args)
        {
            if (stop)
                return GraceObject.Done;
            int theKey = callKey++;

            var stream = new MemoryStream(10240);
            var jsonWriter = JsonReaderWriterFactory.CreateJsonWriter(
                    stream
                );
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("root");
            root.SetAttribute("type", "object");
            xmlDoc.AppendChild(root);

            var mode = xmlDoc.CreateElement("mode");
            var tn = xmlDoc.CreateTextNode("call");
            mode.AppendChild(tn);
            root.AppendChild(mode);

            var rec = xmlDoc.CreateElement("receiver");
            rec.SetAttribute("type", "number");
            tn = xmlDoc.CreateTextNode("" + receiver);
            rec.AppendChild(tn);
            root.AppendChild(rec);

            var key = xmlDoc.CreateElement("key");
            key.SetAttribute("type", "number");
            tn = xmlDoc.CreateTextNode("" + theKey);
            key.AppendChild(tn);
            root.AppendChild(key);

            var nameEl = xmlDoc.CreateElement("name");
            tn = xmlDoc.CreateTextNode("" + name);
            nameEl.AppendChild(tn);
            root.AppendChild(nameEl);

            var arguments = xmlDoc.CreateElement("args");
            arguments.SetAttribute("type", "array");
            foreach (var a in args)
            {
                var item = xmlDoc.CreateElement("item");
                arguments.AppendChild(item);
                if (a is string)
                {
                    tn = xmlDoc.CreateTextNode((string)a);
                    item.AppendChild(tn);
                }
                else if (a is GraceNumber)
                {
                    tn = xmlDoc.CreateTextNode(((GraceNumber)a).Double
                            .ToString());
                    item.AppendChild(tn);
                    item.SetAttribute("type", "number");
                }
                else if (a is GraceBlock)
                {
                    var b = (GraceBlock)a;
                    int id = getBlockId(b);
                    item.SetAttribute("type", "object");
                    var obj = xmlDoc.CreateElement("callback");
                    tn = xmlDoc.CreateTextNode("" + id);
                    obj.AppendChild(tn);
                    item.AppendChild(obj);
                }
                else if (a is int[])
                {
                    var id = ((int[])a)[0];
                    item.SetAttribute("type", "object");
                    var obj = xmlDoc.CreateElement("key");
                    tn = xmlDoc.CreateTextNode("" + id);
                    obj.AppendChild(tn);
                    item.AppendChild(obj);
                }
            }
            root.AppendChild(arguments);

#if DEBUG_WS
            Console.WriteLine(xmlDoc.OuterXml);
#endif
            xmlDoc.WriteTo(jsonWriter);
            jsonWriter.Flush();
#if DEBUG_WS
            Console.WriteLine(
                "Capacity = {0}, Length = {1}, Position = {2}\n",
                stream.Capacity.ToString(),
                stream.Length.ToString(),
                stream.Position.ToString());
#endif
            stream.Seek(0, SeekOrigin.Begin);
            var reader = new StreamReader(stream);
            EventWaitHandle handle = new EventWaitHandle(false,
                    EventResetMode.ManualReset);
            WebSocketEndpoint.AddEventHandle(theKey, handle);
            wss.Send(reader.ReadToEnd());
            handle.WaitOne();
            return WebSocketEndpoint.GetEventResult(theKey);
        }

        public GraceObject SendRPC(int receiver, string name,
                object[] args)
        {
            return SendCall(receiver, name, args);
        }

        public void ReceiveCallback(int blockID, object[] args)
        {
            var c = new Callback(blocks[blockID], args);
            callbacks.Add(c);
        }

        public bool AwaitRemoteCallback(int time,
                out GraceObject block,
                out object[] args)
        {
            Callback c;
            if (!callbacks.TryTake(out c, time))
            {
                block = null;
                args = new object[0];
                return false;
            }
            block = c.block;
            args = c.args;
            return true;
        }

    }

}