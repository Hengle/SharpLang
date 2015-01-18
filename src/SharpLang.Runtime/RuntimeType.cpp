#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "RuntimeType.h"

extern "C" bool isInstInterface(const EEType* eeType, const EEType* expectedInterface)
{
	auto currentInterface = eeType->interfaceMap;
	for (int i = 0; i < eeType->interfacesCount; ++i)
	{
		if (*currentInterface == expectedInterface)
			return true;
		currentInterface++;
	}

	return false;
}

extern "C" void* allocObject(size_t size)
{
    return memset(malloc(size), 0, size);
}

typedef struct IMTEntry
{
	void* methodId;
	void* methodPointer;
} IMTEntry;

extern "C" void* resolveInterfaceCall(void* methodId, void* content)
{
	void* result;

	if (((size_t)content & 1) == 0)
	{
		// Fast path: only one entry in this IMT slot
		result = (void*)content;
	}
	else
	{
		// Normal path: multiple entry in this IMT slot, go through the list
		IMTEntry* imtEntry = (IMTEntry*)((size_t)content & ~1);
		while (imtEntry->methodId != methodId && imtEntry->methodId != 0) { imtEntry++; }

		result = imtEntry->methodPointer;

		// TODO: Slow path: dispatch to add new entry (should only happen with either variance or generics methods)
		//assert(result != 0);
	}

	return result;
}