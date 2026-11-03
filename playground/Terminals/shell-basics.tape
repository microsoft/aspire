# Run from playground/Terminals against the idle Bash resource (Linux/macOS):
# aspire terminal tape play shell --tape-file shell-basics.tape
# Records screens to shell-basics.txt in this directory.
# Move any previous recording before replaying; existing files are not overwritten.

Output shell-basics.txt
Set TypingSpeed 30ms
Set WaitTimeout 10s

Wait+Line /[$#>]$/
Type "printf 'Hello from %s\n' 'Aspire tape playback'"
Enter
Wait+Screen /Hello from Aspire tape playback/

Wait+Line /[$#>]$/
Type "printf 'Arithmetic: 7 + 5 = %s\n' $((7 + 5))"
Enter
Wait+Screen /Arithmetic: 7 [+] 5 = 12/

Wait+Line /[$#>]$/
Type "printf 'Working %s: ' directory; pwd"
Enter
Wait+Screen /Working directory:/

Wait+Line /[$#>]$/
Type "printf 'Sequence %s\n' complete"
Enter
Wait+Screen /Sequence complete/
Wait+Line /[$#>]$/
