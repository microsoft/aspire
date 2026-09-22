# Run from playground/Terminals against an idle playground REPL:
# aspire terminal tape play repl --replica 0 --tape-file repl-basics.tape
# Records screens to repl-basics.txt in this directory.
# Move any previous recording before replaying; existing files are not overwritten.

Output repl-basics.txt
Set TypingSpeed 50ms
Set WaitTimeout 10s

Wait+Line /repl#[0-9]+>$/
Type "help"
Enter
Wait+Screen /Available commands:/

Wait+Line /repl#[0-9]+>$/
Type "whoami"
Enter
Wait+Screen /repl pid [0-9]+/

Wait+Line /repl#[0-9]+>$/
Type "size"
Enter
Wait+Screen /[0-9]+ cols x [0-9]+ rows/

Wait+Line /repl#[0-9]+>$/
Type "time"
Enter
Wait+Screen /[0-9]+-[0-9]+-[0-9]+ [0-9]+:[0-9]+:[0-9]+/
Wait+Line /repl#[0-9]+>$/
