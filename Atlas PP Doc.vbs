' Atlas PP Doc - launches the pipeline UI with zero flash of any console
' window (unlike a .cmd/.bat launcher, WScript itself has no console).
Dim shell, scriptDir, target
Set shell = CreateObject("WScript.Shell")
scriptDir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
target = """" & scriptDir & "\PipelineUI\RunPipeline.ps1" & """"
shell.Run "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " & target, 0, False
