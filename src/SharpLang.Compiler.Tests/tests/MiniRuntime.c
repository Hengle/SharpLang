// Temporary minimal runtime library for testing purpose.
// It should gradually be replaced by a real runtime.

#include <stdio.h>
#include <stdint.h>

// Match runtime String struct layout
typedef struct String
{
	uint32_t length;
	char* value;
} String;

// void System.Console.WriteLine(string)
void System_Void_System_Console__WriteLine_System_String_(String str)
{
	printf("%.*s\n", str.length, str.value);
}
