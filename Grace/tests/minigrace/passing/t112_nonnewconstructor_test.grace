class aCat(name') {
    def name = name'
    method describe {
        print "A cat called {name}"
    }
}

def myCat = aCat "Timothy"
def yourCat = aCat "Gregory"
myCat.describe
yourCat.describe
