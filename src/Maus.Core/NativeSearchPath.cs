using System.Runtime.InteropServices;

// Sécurité (règle 10 de la NASA, avertissement CA5392, 12/10/2026) : toutes les bibliothèques de Windows appelées par ce
// projet (user32, kernel32, advapi32, dismapi, pdh…) sont cherchées seulement dans System32. Un fichier du même nom posé
// dans le dossier courant ou dans le PATH ne peut pas être chargé à leur place, avec les droits administrateur de MAUS.
// Les bibliothèques hors de System32 (nvml.dll) sont chargées par leur chemin complet (NativeLibrary.TryLoad).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
