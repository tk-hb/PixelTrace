@echo off
setlocal
pushd "%~dp0"
dotnet build PixelTrace.slnx -c Debug -p:EmbedAssembliesIntoApk=true %*
set "buildResult=%ERRORLEVEL%"
if "%buildResult%"=="0" echo APK: PixelTrace\bin\Debug\net10.0-android\android-arm64\com.tkhb.pixeltrace-Signed.apk
popd
exit /b %buildResult%
