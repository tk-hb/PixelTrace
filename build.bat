@echo off
setlocal
pushd "%~dp0"
dotnet build PixelTrace.slnx -c Release -p:EmbedAssembliesIntoApk=true %*
set "buildResult=%ERRORLEVEL%"
if not "%buildResult%"=="0" goto finished
set "signedApk=PixelTrace\bin\Release\net10.0-android\android-arm64\com.tkhb.pixeltrace-Signed.apk"
if not exist "%signedApk%" (
    echo Signed Release APK was not created: %signedApk%
    set "buildResult=1"
    goto finished
)
if not exist "artifacts" mkdir "artifacts"
copy /y "%signedApk%" "artifacts\PixelTrace-Release.apk" >nul
if errorlevel 1 (
    set "buildResult=1"
    goto finished
)
echo APK: artifacts\PixelTrace-Release.apk
:finished
popd
exit /b %buildResult%
