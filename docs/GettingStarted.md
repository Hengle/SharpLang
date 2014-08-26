# Getting started

## Compiling on Windows/Visual Studio

1. Clone SharpLang to `<SharpLang>` with submodules.
2. Download `http://sourceforge.net/projects/mingw-w64/files/Toolchains%20targetting%20Win32/Personal%20Builds/mingw-builds/4.9.0/threads-win32/dwarf/i686-4.9.0-release-win32-dwarf-rt_v3-rev2.7z/download` and extract it in `<SharpLang>\deps`.
3. Download and install [CMake](http://www.cmake.org/cmake/resources/software.html). Alternatively, extract the zip archive and add `bin` directory to PATH.
4. Download and install [python](https://www.python.org/downloads/). Alternatively, set `PYTHON_EXECUTABLE:FILEPATH` variable in `<SharpLang>\deps\llvm\build\CMakeCache.txt` to `<SharpLang>/deps/mingw32/opt/bin/python.exe`.
5.  Run `<SharpLang>\deps\build_llvm_clang_vs2013.bat`. This will build LLVM and Clang both in RelWithDebInfo and Debug mode with VS2013.
6. Run `<SharpLang>\build\GenerateProjects.bat`.
7. Open `<SharpLang>\build\vs2013\SharpLang.sln`.
8. Switch Active solution platform to *x86* in Build > Configuration Manager.
9. Build and play with tests.
