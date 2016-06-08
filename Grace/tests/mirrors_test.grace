import "mirrors" as m

method assert(expr:Boolean) {
    if (expr.not) then {
        Exception.raise "Failed"
    }
}

def testString = "asdf"
def testNumber = 42
def testClass = object {
}

def objectMirror = m.reflect(testString)

def methods = objectMirror.methodNames

methods.do { name -> print(name) }

// TODO: figure out why we assignment does not work
//def tryMethod = objectMirror.getMethod("asString")

// test nullary method request
assert (objectMirror.getMethod("asString").request([]) == testString.asString)

// test single part, single arg method request and equality
assert (objectMirror.getMethod("==(_)").request([testString]))

// test multi part, one arg per part method request
assert (objectMirror.getMethod("substringFrom(_) to(_)").request([1,2]) == testString.substringFrom(1) to(2))

// test LookupError is being thrown
try {
    var dne := objectMirror.getMethod("doesnotexist")
} catch {
    e : LookupError -> print "OK; Caught an error."
}

