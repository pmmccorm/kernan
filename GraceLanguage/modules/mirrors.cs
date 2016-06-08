using Grace.Execution;
using Grace.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grace.modules
{
    [ModuleEntryPoint]
    public class mirrors : GraceObject
    {
        public static GraceObject Instantiate(EvaluationContext ctx)
        {
            return new mirrors();
        }

        private mirrors() : base("mirrors")
        {
            AddMethod("reflect(_)", new DelegateMethod1(mReflect));
        }

        private GraceObject mReflect(GraceObject o)
        {
            return new Mirror(o);
        }
    }

    public class Mirror : GraceObject
    {
        private List<String> methodNames;
        private Dictionary<String, Method> methods;
        private GraceObject obj;

        public Mirror (GraceObject o)
        {
            methodNames = o.MethodNames();
            methods = o.objectMethods;
            obj = o;
            AddMethod("methods",new DelegateMethod0(mMethods));
            AddMethod("methodNames", new DelegateMethod0(mMethodNames));
            AddMethod("getMethod(_)", new DelegateMethod1Ctx(mGetMethod));
        }

        private GraceObject mMethods()
        {
            GraceVariadicList list = new GraceVariadicList();
            foreach (KeyValuePair<string, Method> entry in methods) {
                list.Add(new MethodMirror(obj, entry.Key, entry.Value));
            }
            return list;
        }

        private GraceObject mMethodNames()
        {
            GraceVariadicList list = new GraceVariadicList();
            foreach (var m in methodNames) {
                list.Add(GraceString.Create(m));
            }
            return list;
        }

        private GraceObject mGetMethod(EvaluationContext ctx, GraceObject methodName)
        {
            GraceString name = methodName as GraceString;
            if (name == null)
                ErrorReporting.RaiseError(ctx, "R2001",
                    new Dictionary<string, string> {
                        { "method", "getMethod" },
                        { "required", "String" },
                        { "index", "0" },
                        { "part", "getMethod" }
                    },
                    "ArgumentTypeError: method names must be a string"
                );

            String method = name.Value;
            if (!methods.ContainsKey(method))
                ErrorReporting.RaiseError(ctx, "R2000",
                    new Dictionary<string, string> {
                        { "method", method },
                        { "receiver",  obj.ToString() }
                    },
                    "LookupError: method not found"
                );

            return new MethodMirror (obj, method, methods[method]);
        }
    }

    public class MethodMirror : GraceObject
    {
        private string name;
        private Method method;
        private GraceObject receiver;
        private int paramCount;

        public MethodMirror (GraceObject obj, String name, Method method)
        {
            this.name = name;
            this.method = method;
            this.receiver = obj;
            this.paramCount = name.Count(c => c == '_');

            AddMethod("name", new DelegateMethod0(mName));
            AddMethod("asString", new DelegateMethod0(mName));
            AddMethod("partcount", new DelegateMethod0(mPartCount));
            AddMethod("paramcounts", new DelegateMethod0(mParamCounts));
            AddMethod("request(_)", new DelegateMethod1Ctx(mRequest));
        }

        private GraceObject mName()
        {
            return GraceString.Create(name);
        }

        private GraceObject mPartCount()
        {
            int count = name.Count(c => c == ' ');

            if (count == 0)
                ++count;

            return GraceNumber.Create(count);
        }

        private GraceObject mParamCounts()
        {
            return GraceNumber.Create(paramCount);
        }

        // IDEA: it might be nice to move method and part parsing functions into Methods.cs
        // TODO: R2004 isn't really the right error, it and r2006 shuould be refinements of
        //       some more general argument error
        private GraceObject mRequest(EvaluationContext ctx, GraceObject obj)
        {
            GraceVariadicList arglist = obj as GraceVariadicList;
            
            if (arglist == null)
                ErrorReporting.RaiseError(ctx, "R2001",
                        new Dictionary<string, string> {
                            { "method", "request" },
                            { "required", "Lineup" },
                            { "index", "0" },
                            { "part", "request" },
                        },
                        "ArgumentTypeError: must supply an enumerable"
                    );

            if (arglist.elements.Count != paramCount)
                ErrorReporting.RaiseError(ctx, "R2004",
                        new Dictionary<string, string> {
                            { "part", "?" },
                            { "method", this.name },
                            { "need", paramCount.ToString() },
                            { "have", arglist.elements.Count.ToString() },
                        },
                        "InsufficientArgumentsError: must supply correct number of arguments"
                    );

            MethodRequest req = new MethodRequest();
            foreach (string part in name.Split(' '))
            {
                int count = part.Count(c => c == '_');
                RequestPart rp = new RequestPart(part, null, arglist.elements.GetRange(0, count));
                req.AddPart(rp);
                arglist.elements.RemoveRange(0, count);
            }
            
            return method.Respond(ctx, receiver, req);

        }
    }
}
