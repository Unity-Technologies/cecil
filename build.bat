set BASEDIR=%~dp0
set BUILDDIR=%BASEDIR%builds
set LIBDIR=%BUILDDIR%\lib
set LIBDIR40=%LIBDIR%\net40
set MSBUILD=dotnet msbuild
set SEVENZIP="C:\Program Files\7-Zip\7z.exe"

::Setup Directories
mkdir %BUILDDIR%
mkdir %LIBDIR%
mkdir %LIBDIR40%

::Create Version TXT
cd %BASEDIR%
type NUL > %BUILDDIR%\version.txt
@echo | set /p="Repository: " >> %BUILDDIR%\version.txt
git remote get-url origin >> %BUILDDIR%\version.txt

@echo | set /p="Branch: " >> %BUILDDIR%\version.txt
git branch >> %BUILDDIR%\version.txt

@echo | set /p="Commit: " >> %BUILDDIR%\version.txt
git log -n 1 --pretty=format:%%H >> %BUILDDIR%\version.txt

::Build Cecil for .NET 4.0
%MSBUILD% "%BASEDIR%\Mono.Cecil.sln" /t:Build /p:Configuration=net_4_0_Release /p:Platform="Any CPU" /p:OutputPath=%LIBDIR40% || goto failure
call:CleanupBuild %LIBDIR40%

%SEVENZIP% a -m0=lzma -r %BASEDIR%/builds.7z %BUILDDIR%/* || goto failure

goto:eof

:failure
echo Last failure exit code %errorlevel%
exit /b 1


goto:eof
