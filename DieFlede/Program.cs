﻿#region BSD license
/*
Copyright © 2015, KimikoMuffin.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer. 
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. The names of its contributors may not be used to endorse or promote 
   products derived from this software without specific prior written 
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using DieFledermaus.Cli.Globalization;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Utilities.Encoders;

namespace DieFledermaus.Cli
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.Write(TextResources.Title);
            Console.Write(' ');
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
                .OfType<System.Reflection.AssemblyFileVersionAttribute>().FirstOrDefault().Version);
            Console.WriteLine();
            Console.WriteLine(TextResources.Disclaimer);
            Console.WriteLine();

            ClParser parser = new ClParser();
            ClParamFlag create = new ClParamFlag(parser, TextResources.HelpMCreate, 'c', "create", TextResources.PNameCreate);
            ClParamFlag extract = new ClParamFlag(parser, TextResources.HelpMExtract, 'x', "extract", TextResources.PNameExtract);
            ClParamFlag list = new ClParamFlag(parser, TextResources.HelpMList, 'l', "list", TextResources.PNameList);
            ClParamFlag help = new ClParamFlag(parser, TextResources.HelpMHelp, 'h', "help", TextResources.PNameHelp);
            extract.MutualExclusives.Add(create);
            create.MutualExclusives.Add(extract);
            create.MutualExclusives.Add(list);
            create.OtherMessages.Add(list, NoOutputCreate);

            ClParamEnum<MausCompressionFormat> cFormat;
            {
                Dictionary<string, MausCompressionFormat> locArgs = new Dictionary<string, MausCompressionFormat>();
                locArgs.Add("DEFLATE", MausCompressionFormat.Deflate);
                locArgs.Add("LZMA", MausCompressionFormat.Lzma);
                locArgs.Add(TextResources.PFormatNone, MausCompressionFormat.None);

                Dictionary<string, MausCompressionFormat> unArgs = new Dictionary<string, MausCompressionFormat>() { { "none", MausCompressionFormat.None } };

                cFormat = new ClParamEnum<MausCompressionFormat>(parser, TextResources.HelpMFormat, locArgs, unArgs, '\0', "format", TextResources.PNameFormat);
                cFormat.MutualExclusives.Add(extract);
                cFormat.MutualExclusives.Add(list);
                cFormat.OtherMessages.Add(extract, NoEntryExtract);
                cFormat.OtherMessages.Add(list, NoEntryExtract);
            }

            ClParamFlag single = new ClParamFlag(parser, TextResources.HelpMSingle, 'n', "single", TextResources.PNameSingle);

            ClParamFlag interactive = new ClParamFlag(parser, TextResources.HelpMInteractive, 'i', "interactive", TextResources.PNameInteractive);

            ClParamFlag overwrite = new ClParamFlag(parser, TextResources.HelpMOverwrite, 'w', "overWrite", TextResources.PNameOverwrite);
            ClParamFlag skipexist = new ClParamFlag(parser, TextResources.HelpMSkip, 's', "skip", "skip-existing",
                TextResources.PNameSkip, TextResources.PNameSkipExisting);
            skipexist.MutualExclusives.Add(overwrite);

            ClParamFlag verbose = new ClParamFlag(parser, TextResources.HelpMVerbose, 'v', "verbose", TextResources.PNameVerbose);

            ClParamValue archiveFile = new ClParamValue(parser, TextResources.HelpMArchive, TextResources.HelpArchive, 'f', "file", "archive",
                TextResources.PNameFile, TextResources.PNameArchive);
            archiveFile.ConvertValue = Path.GetFullPath;

            ClParamMulti entryFile = new ClParamMulti(parser, string.Join(Environment.NewLine, TextResources.HelpMEntry, TextResources.HelpMEntry2),
                TextResources.HelpInput, 'e');
            parser.RawParam = entryFile;
            ClParamMulti entryPath = new ClParamMulti(parser, string.Join(Environment.NewLine, TextResources.HelpMEntryPath1, TextResources.HelpMEntryPath2,
                TextResources.HelpMEntryPath3), TextResources.HelpPath, 'p');

            ClParamValue outFile = new ClParamValue(parser, TextResources.HelpMOut, TextResources.HelpOutput, 'o', "out", "output",
                TextResources.PNameOut, TextResources.PNameOutput);
            outFile.ConvertValue = Path.GetFullPath;
            outFile.MutualExclusives.Add(create);
            create.OtherMessages.Add(outFile, NoOutputCreate);

            outFile.MutualExclusives.Add(entryFile);
            entryFile.MutualExclusives.Add(outFile);

            ClParamEnum<MausHashFunction> hash;
            {
                Dictionary<string, MausHashFunction> locArgs =
                    ((MausHashFunction[])Enum.GetValues(typeof(MausHashFunction))).ToDictionary(i => i.ToString());
                hash = new ClParamEnum<MausHashFunction>(parser, TextResources.HelpMHash, locArgs, new Dictionary<string, MausHashFunction>(), 'H',
                    "hash", "hash-funcs", TextResources.PNameHash, TextResources.PNameHashFunc);
            }

            ClParamEnum<MausEncryptionFormat> cEncFmt;
            {
                Dictionary<string, MausEncryptionFormat> locArgs = new Dictionary<string, MausEncryptionFormat>()
                {
                    { "AES", MausEncryptionFormat.Aes },
                    { "Twofish", MausEncryptionFormat.Twofish },
                    { "Threefish", MausEncryptionFormat.Threefish }
                };
                cEncFmt = new ClParamEnum<MausEncryptionFormat>(parser, TextResources.HelpMEncFmt, locArgs,
                    new Dictionary<string, MausEncryptionFormat>(), '\0', "encryption", TextResources.PNameEncFmt);
            }

            ClParamFlag hide = new ClParamFlag(parser, TextResources.HelpMHide, '\0', "hide", TextResources.PNameHide);
            extract.MutualExclusives.Add(hide);
            extract.OtherMessages.Add(hide, NoEntryExtract);

            ClParamValue sigKey = new ClParamValue(parser, TextResources.HelpMSigKey, TextResources.HelpPath, '\0', "signature-key", TextResources.PNameSigKey);
            ClParamValue sigDex = new ClParamValue(parser, TextResources.HelpMSigDex, TextResources.HelpIndex, '\0', "signature-index", TextResources.PNameSigDex);
            ClParamValue encKey = new ClParamValue(parser, TextResources.HelpMEncKey, TextResources.HelpPath, '\0', "encryption-key", TextResources.PNameEncKey);
            ClParamValue encDex = new ClParamValue(parser, TextResources.HelpMEncDex, TextResources.HelpPath, '\0', "encryption-idnex", TextResources.PNameEncDex);

            ClParam[] clParams = parser.Params.ToArray();

            if (args.Length == 1 && args[0][0] != '-')
            {
                archiveFile.IsSet = true;
                archiveFile.Value = args[0];
                extract.IsSet = true;
            }
            else if (parser.Parse(args))
            {
                ShowHelp(clParams, false);
                return Return(-1, interactive);
            }
            bool acting = false;

            if (args.Length > 0)
            {
                if (extract.IsSet || list.IsSet)
                {
                    if (!archiveFile.IsSet)
                    {
                        Console.Error.WriteLine(TextResources.ExtractNoArchive);
                        ShowHelp(clParams, false);
                        return Return(-1, interactive);
                    }
                    else if (!File.Exists(archiveFile.Value))
                    {
                        Console.Error.WriteLine(TextResources.FileNotFound, archiveFile.Value);
                        ShowHelp(clParams, false);
                        return Return(-1, interactive);
                    }

                    if (extract.IsSet)
                    {
                        if (outFile.IsSet && !Directory.Exists(outFile.Value))
                        {
                            Console.Error.WriteLine(TextResources.DirNotFound, outFile.Value);
                            ShowHelp(clParams, false);
                            return Return(-1, interactive);
                        }
                    }
                    else if (outFile.IsSet)
                    {
                        Console.Error.WriteLine(TextResources.OutDirOnly);
                        ShowHelp(clParams, false);
                        return Return(-1, interactive);
                    }

                    acting = true;
                }
                else if (create.IsSet)
                {
                    if (!entryFile.IsSet)
                    {
                        Console.Error.WriteLine(TextResources.CreateNoEntry);
                        ShowHelp(clParams, false);
                        return Return(-1, interactive);
                    }

                    if (!single.IsSet && !archiveFile.IsSet)
                    {
                        Console.Error.WriteLine(TextResources.ExtractNoArchive);
                        ShowHelp(clParams, false);
                        return Return(-1, interactive);
                    }

                    if (cEncFmt.IsSet)
                    {
                        if (!interactive.IsSet)
                        {
                            Console.Error.WriteLine(TextResources.EncryptionNoOpts);
                            ShowHelp(clParams, false);
                            return -1;
                        }
                    }
                    else if (hide.IsSet)
                    {
                        Console.Error.WriteLine(TextResources.ErrorHide, hide.Key);
                        ShowHelp(clParams, false);
                        return Return(-1, interactive);
                    }
                    acting = true;
                }
                else if (archiveFile.IsSet || entryFile.IsSet || outFile.IsSet)
                {
                    Console.Error.WriteLine(TextResources.RequireAtLeastOne, "-c, -x, -l");
                    ShowHelp(clParams, false);
                    return Return(-1, interactive);
                }
                else if (!help.IsSet)
                {
                    Console.Error.WriteLine(TextResources.RequireAtLeastOne, "-c, -x, -l, --help");
                    ShowHelp(clParams, false);
                    return Return(-1, interactive);
                }
            }

            if (help.IsSet || args.Length == 0)
            {
                bool showFull = help.IsSet && !acting;

                ShowHelp(clParams, showFull);
                if (showFull || args.Length == 0)
                    return Return(0, interactive);
            }

            AsymmetricKeyParameter keyObj;

            if (sigKey.IsSet)
            {
                BigInteger index;

                if (sigDex.IsSet)
                {
                    index = ReadIndex(sigDex);

                    if (index == null)
                        return Return(-7, interactive);
                }
                else index = null;

                keyObj = PublicKeyReadFuncs.LoadKeyFile(sigKey.Value, index, create.IsSet, new PasswordLoader(interactive), false);

                if (keyObj == null)
                    return Return(-7, interactive);
            }
            else if (sigDex.IsSet)
            {
                Console.Error.WriteLine(TextResources.SigDexNeedsKey);
                return Return(-7, interactive);
            }
            else keyObj = null;

            RsaKeyParameters encObj;
            if (encKey.IsSet)
            {
                if (create.IsSet && !cEncFmt.IsSet)
                {
                    Console.Error.WriteLine(TextResources.EncKeyNeedsEnc);
                    return Return(-8, interactive);
                }

                BigInteger index;
                if (encDex.IsSet)
                {
                    index = ReadIndex(encDex);

                    if (index == null)
                        return Return(-8, interactive);
                }
                else index = null;

                object kObj = PublicKeyReadFuncs.LoadKeyFile(encKey.Value, index, !create.IsSet, new PasswordLoader(interactive), true);
                if (kObj == null)
                    return Return(-8, interactive);

                encObj = kObj as RsaKeyParameters;
                if (encObj == null)
                {
                    Console.Error.WriteLine(TextResources.EncKeyNeedsRsa);
                    return Return(-8, interactive);
                }
            }
            else if (encDex.IsSet)
            {
                if (!cEncFmt.IsSet)
                {
                    Console.Error.WriteLine(TextResources.EncDexNeedsEnc);
                    return Return(-8, interactive);
                }

                Console.Error.WriteLine(TextResources.EncDexNeedsKey);
                return Return(-8, interactive);

            }
            else encObj = null;

            string ssPassword = null;
            List<FileStream> streams = null;

            string archivePath = null, archiveTemp = null;
            try
            {
                if (create.IsSet)
                {
                    archivePath = Path.GetFullPath(archiveFile.Value);
                    archiveTemp = GetTempPath(archivePath);

                    Dictionary<string, string> allFilePaths;
                    if (GetEntryPaths(single.IsSet, archivePath, entryFile, entryPath, parser.OrderedParams, null, out allFilePaths))
                        return Return(-1, interactive);

                    #region Create - Single
                    if (single.IsSet)
                    {
                        var curKVP = allFilePaths.First();

                        string entry = curKVP.Key;

                        FileInfo entryInfo = new FileInfo(entry);
                        using (FileStream fs = File.OpenRead(entry))
                        {
                            MausCompressionFormat compFormat = cFormat.Value.HasValue ? cFormat.Value.Value : MausCompressionFormat.Deflate;
                            MausEncryptionFormat encFormat = MausEncryptionFormat.None;

                            if (OverwritePrompt(interactive, overwrite, skipexist, verbose, ref archiveFile.Value))
                                return Return(-3, interactive);

                            if (CreateEncrypted(cEncFmt, encObj, interactive, out encFormat, out ssPassword))
                                return Return(-4, interactive);

                            if (archiveFile.Value == null)
                                archiveFile.Value = entry + mausExt;

                            if (archiveFile.Value == entry)
                            {
                                Console.WriteLine(TextResources.OverwriteSameEntry, entry);
                                return Return(-3, interactive);
                            }

                            using (Stream arStream = File.Create(archiveTemp))
                            using (DieFledermausStream ds = new DieFledermausStream(arStream, compFormat, encFormat))
                            {
                                ds.RSASignParameters = keyObj as RsaKeyParameters;
                                ds.DSASignParameters = keyObj as DsaKeyParameters;
                                ds.ECDSASignParameters = keyObj as ECKeyParameters;
                                ds.RSAEncryptParameters = encObj;

                                if (hash.Value.HasValue)
                                    ds.HashFunction = hash.Value.Value;

                                if (ssPassword != null)
                                    ds.Password = ssPassword;

                                try
                                {
                                    ds.CreatedTime = entryInfo.CreationTimeUtc;
                                }
                                catch
                                {
                                    if (verbose.IsSet)
                                        Console.WriteLine(TextResources.TimeCGet, entryInfo.FullName);
                                }

                                try
                                {
                                    ds.ModifiedTime = entryInfo.LastWriteTimeUtc;
                                }
                                catch
                                {
                                    if (verbose.IsSet)
                                        Console.WriteLine(TextResources.TimeMGet, entryInfo.FullName);
                                }

                                ds.Filename = curKVP.Value;
                                fs.CopyTo(ds);
                            }
                        }
                    }
                    #endregion
                    #region Create - Archive
                    else
                    {
                        MausCompressionFormat compFormat = cFormat.Value.HasValue ? cFormat.Value.Value : MausCompressionFormat.Deflate;
                        streams = new List<FileStream>(allFilePaths.Count);
                        List<FileInfo> fileInfos = new List<FileInfo>(streams.Capacity);
                        List<string> entryNames = new List<string>(streams.Capacity);

                        foreach (var curKVP in allFilePaths)
                        {
                            string curEntry = curKVP.Key;

                            var curInfo = new FileInfo(curEntry);
                            if (!curInfo.Exists)
                            {
                                Console.Error.WriteLine(TextResources.FileNotFound, curEntry);
                                continue;
                            }

                            try
                            {
                                streams.Add(File.OpenRead(curEntry));
                                fileInfos.Add(curInfo);
                                entryNames.Add(curKVP.Value);
                            }
                            catch (Exception e)
                            {
                                Console.Error.WriteLine(e.Message);
#if DEBUG
                                GoThrow(e);
#endif
                            }
                        }
                        if (streams.Count == 0)
                        {
                            Console.Error.WriteLine(TextResources.NoEntriesCreate);
                            return Return(-1, interactive);
                        }

                        if (OverwritePrompt(interactive, overwrite, skipexist, verbose, ref archiveFile.Value))
                            return Return(-3, interactive);

                        MausEncryptionFormat encFormat = MausEncryptionFormat.None;
                        if (CreateEncrypted(cEncFmt, encObj, interactive, out encFormat, out ssPassword))
                            return Return(-4, interactive);

                        using (FileStream fs = File.Create(archiveTemp))
                        using (DieFledermauZArchive archive = new DieFledermauZArchive(fs, hide.IsSet ? encFormat : MausEncryptionFormat.None))
                        {
                            archive.RSASignParameters = archive.DefaultRSASignParameters = keyObj as RsaKeyParameters;
                            archive.DSASignParameters = archive.DefaultDSASignParameters = keyObj as DsaKeyParameters;
                            archive.ECDSASignParameters = archive.DefaultECDSASignParameters = keyObj as ECKeyParameters;
                            if (hide.IsSet)
                                archive.RSAEncryptParameters = encObj;

                            if (hash.Value.HasValue)
                                archive.HashFunction = hash.Value.Value;
                            if (hide.IsSet)
                                archive.Password = ssPassword;

                            for (int i = 0; i < fileInfos.Count; i++)
                            {
                                var curInfo = fileInfos[i];

                                DieFledermauZArchiveEntry entry = archive.Create(entryNames[i], compFormat, hide.IsSet ? MausEncryptionFormat.None : encFormat);

                                if (verbose.IsSet)
                                    entry.Progress += Entry_Progress;

                                if (cEncFmt.IsSet && !hide.IsSet)
                                {
                                    entry.Password = ssPassword;
                                    entry.RSAEncryptParameters = encObj;
                                }

                                using (Stream writeStream = entry.OpenWrite())
                                    streams[i].CopyTo(writeStream);

                                try
                                {
                                    entry.CreatedTime = curInfo.CreationTimeUtc;
                                }
                                catch
                                {
                                    if (verbose.IsSet)
                                        Console.Error.WriteLine(TextResources.TimeCGet, curInfo.FullName);
                                }
                                try
                                {
                                    entry.ModifiedTime = curInfo.LastWriteTimeUtc;
                                }
                                catch
                                {
                                    if (verbose.IsSet)
                                        Console.Error.WriteLine(TextResources.TimeMGet, curInfo.FullName);
                                }
                            }
                            if (verbose.IsSet)
                                Console.WriteLine();
                        }
                    }
                    #endregion

                    if (File.Exists(archivePath))
                        File.Delete(archivePath);
                    File.Move(archiveTemp, archivePath);

                    if (verbose.IsSet)
                        Console.WriteLine(TextResources.Completed);
                    return Return(0, interactive);
                }

                #region Extract/List
                using (FileStream fs = File.OpenRead(archiveFile.Value))
                using (DieFledermauZArchive dz = new DieFledermauZArchive(fs, MauZArchiveMode.Read))
                {
                    if (dz.EncryptionFormat != MausEncryptionFormat.None)
                    {
                        if (!interactive.IsSet && encObj == null)
                        {
                            Console.Error.WriteLine(TextResources.EncryptedEx);
                            Console.Error.WriteLine(TextResources.EncryptionNoOptsEx);
                            return -4;
                        }

                        Console.WriteLine(TextResources.EncryptedEx);

                        if (EncryptionPrompt(dz, encObj, interactive, out ssPassword))
                            return Return(-4, interactive);
                    }

                    #region Archive Signature
                    if (dz.IsRSASigned && keyObj is RsaKeyParameters)
                    {
                        dz.RSASignParameters = (RsaKeyParameters)keyObj;
                        if (dz.VerifyRSASignature())
                            Console.WriteLine(TextResources.SignRSAArchiveVerified);
                        else
                            Console.WriteLine(TextResources.SignRSAArchiveUnverified);
                        Console.WriteLine();
                    }

                    if (dz.IsDSASigned && keyObj is DsaKeyParameters)
                    {
                        dz.DSASignParameters = (DsaKeyParameters)keyObj;
                        if (dz.VerifyDSASignature())
                            Console.WriteLine(TextResources.SignDSAArchiveVerified);
                        else
                            Console.WriteLine(TextResources.SignDSAArchiveUnverified);
                        Console.WriteLine();
                    }

                    if (dz.IsECDSASigned && keyObj is ECKeyParameters)
                    {
                        dz.ECDSASignParameters = keyObj as ECKeyParameters;
                        if (dz.VerifyECDSASignature())
                            Console.WriteLine(TextResources.SignECDSAArchiveVerified);
                        else
                            Console.WriteLine(TextResources.SignECDSAArchiveUnverified);
                        Console.WriteLine();
                    }
                    #endregion

                    Regex[] matches;

                    if (!entryFile.IsSet)
                        matches = null;
                    else if (dz.IsSingle)
                    {
                        Console.WriteLine(TextResources.WarningSingle);
                        matches = null;
                    }
                    else matches = entryFile.Values.Select(GetRegex).ToArray();

                    #region List
                    if (list.IsSet)
                    {
                        for (int i = 0; i < dz.Entries.Count; i++)
                        {
                            var curEntry = dz.Entries[i];

                            if (DoFailDecrypt(curEntry, matches, encObj, interactive, i, ref ssPassword))
                            {
                                Console.WriteLine(GetName(i, curEntry));
                                continue;
                            }
                            if (curEntry.EncryptionFormat != MausEncryptionFormat.None)
                                curEntry = curEntry.Decrypt();

                            Console.WriteLine(curEntry.Path);

                            VerifySigns(keyObj, curEntry as DieFledermauZArchiveEntry);

                            if (verbose.IsSet)
                            {
                                var curItem = curEntry as DieFledermauZArchiveEntry;
                                if (curItem == null) continue;
                                if (curItem.CreatedTime.HasValue)
                                {
                                    Console.Write(" ");
                                    Console.Write(string.Format(TextResources.TimeC, curItem.CreatedTime.Value));

                                    if (curItem.ModifiedTime.HasValue)
                                        Console.Write(" / ");
                                    else
                                        Console.WriteLine();
                                }
                                if (curItem.ModifiedTime.HasValue)
                                {
                                    if (!curItem.CreatedTime.HasValue)
                                        Console.Write(' ');
                                    Console.WriteLine(TextResources.TimeM, curItem.ModifiedTime.Value);
                                }

                                if (!string.IsNullOrWhiteSpace(curItem.Comment))
                                {
                                    Console.WriteLine(" " + TextResources.Comment);
                                    Console.WriteLine(curItem.Comment.Trim());
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                    #endregion

                    #region Extract
                    if (extract.IsSet)
                    {
                        HashSet<string> paths = new HashSet<string>();
                        for (int i = 0; i < dz.Entries.Count; i++)
                        {
                            var entry = dz.Entries[i];

                            if (!DoFailDecrypt(entry, matches, encObj, interactive, i, ref ssPassword))
                                paths.Add(entry.Decrypt().Path);
                        }
                        if (paths.Count == 0)
                        {
                            Console.Error.WriteLine(TextResources.NoValidEntries);
                            return Return(-1, interactive);
                        }

                        Dictionary<string, string> allFilePaths;
                        if (GetEntryPaths(dz.IsSingle, archivePath, entryFile, entryPath, parser.OrderedParams, paths, out allFilePaths))
                            return Return(-1, interactive);

                        string outDir;
                        if (outFile.IsSet)
                            outDir = outFile.Value;
                        else
                            outDir = Environment.CurrentDirectory;

                        entryFile.Values.Select(i => i.Value).Distinct();

                        foreach (var curKVP in allFilePaths.ToArray())
                        {
                            DieFledermauZItem entry;
                            if (!dz.Entries.TryGetEntry(curKVP.Key, out entry))
                            {
                                //Probably won't happen at all, but ...
                                allFilePaths.Remove(curKVP.Key);
                                continue;
                            }

                            VerifySigns(keyObj, entry as DieFledermauZArchiveEntry);
                        }

                        int extracted = 0;

                        foreach (var curKVP in allFilePaths)
                        {
                            string curPath = curKVP.Value;

                            try
                            {
                                if (OverwritePrompt(interactive, overwrite, skipexist, verbose, ref curPath))
                                    continue;

                                DieFledermauZItem curItem;
                                dz.Entries.TryGetEntry(curKVP.Key, out curItem);

                                var curDir = curItem as DieFledermauZEmptyDirectory;

                                if (curDir != null)
                                {
                                    if (File.Exists(curPath))
                                        File.Delete(curPath);

                                    if (!Directory.Exists(curPath))
                                        Directory.CreateDirectory(curPath);
                                    extracted++;
                                    continue;
                                }

                                var curEntry = curItem as DieFledermauZArchiveEntry;

                                using (Stream curS = curEntry.OpenRead())
                                using (FileStream curFS = File.Create(curPath))
                                    curS.CopyTo(curFS);

                                FileInfo fInfo = new FileInfo(curPath);

                                if (curEntry.CreatedTime.HasValue)
                                {
                                    try
                                    {
                                        fInfo.CreationTimeUtc = curEntry.CreatedTime.Value;
                                    }
                                    catch
                                    {
                                        if (verbose.IsSet)
                                            Console.Error.WriteLine(TextResources.TimeCSet, curPath);
                                    }
                                }
                                if (curEntry.ModifiedTime.HasValue)
                                {
                                    try
                                    {
                                        fInfo.LastWriteTimeUtc = curEntry.ModifiedTime.Value;
                                    }
                                    catch
                                    {
                                        if (verbose.IsSet)
                                            Console.Error.WriteLine(TextResources.TimeMSet, curPath);
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Console.Error.WriteLine(e.Message);
#if DEBUG
                                GoThrow(e);
#endif
                                continue;
                            }
                            extracted++;
                        }

                        if (verbose.IsSet)
                            Console.WriteLine(TextResources.SuccessExtract, extracted, allFilePaths.Count - extracted);
                    }
                    #endregion
                }
                #endregion

                if (verbose.IsSet)
                    Console.WriteLine(TextResources.Completed);
                return Return(0, interactive);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
#if DEBUG
                GoThrow(e);
#endif
                return Return(e.HResult, interactive);
            }
            finally
            {
                if (streams != null)
                {
                    for (int i = 0; i < streams.Count; i++)
                        streams[i].Dispose();
                }
                if (archiveTemp != null && File.Exists(archiveTemp))
                    File.Delete(archiveTemp);
            }
        }

        const string mausExt = ".maus", mauzExt = ".mauz";
        const int mausExtLen = 5;

        private static bool GetEntryPaths(bool isSingle, string archivePath, ClParamMulti entryFile, ClParamMulti entryPath, ClParam[] ordered,
            HashSet<string> paths, out Dictionary<string, string> allFilePaths)
        {
            allFilePaths = new Dictionary<string, string>();

            var chars = Path.GetInvalidFileNameChars();

            if (paths != null && isSingle)
            {
                //Won't be empty because that got checked earlier.
                string solePath = paths.First();

                if (solePath == null)
                {
                    string archExt = Path.GetExtension(archivePath);

                    if (string.Equals(archExt, mausExt, StringComparison.OrdinalIgnoreCase))
                        solePath = archivePath.Substring(0, archivePath.Length - mausExtLen);
                    else
                    {
                        if (string.IsNullOrWhiteSpace(archExt))
                            solePath = archivePath + ".out";
                        else
                            solePath = Path.ChangeExtension(archivePath, ".out." + archExt);
                    }
                }

                if (entryPath.IsSet)
                {
                    var entryPathValues = entryPath.Values;

                    solePath = Path.GetFullPath(entryPathValues[0].Value);

                    for (int i = 1; i < entryPathValues.Length; i++)
                    {
                        if (Path.GetFullPath(entryPathValues[i].Value) != solePath)
                        {
                            Console.Error.WriteLine(TextResources.PathDupSingle);
                            return true;
                        }
                    }
                }
                else solePath = Path.GetFullPath(solePath);
                allFilePaths.Add(string.Empty, solePath);

                return false;
            }

            //TODO: Get this to work nicely with wildcards
            foreach (IndexedString i in entryPath.Values)
            {
                if (i.Index == 0 || ordered[i.Index - 1] != entryFile)
                    Console.Error.WriteLine(TextResources.WarningPath, i.Value);
            }

            IEnumerable<IndexedString> entries;

            if (paths != null)
            {
                if (entryFile.IsSet)
                {
                    List<IndexedString> entryItems = new List<IndexedString>();
                    foreach (IndexedString curItem in entryFile.Values)
                    {
                        string path = curItem.Value.Trim();

                        if (paths.Contains(curItem.Value))
                            entryItems.Add(new IndexedString(path, curItem.Index));
                        else
                            Console.Error.WriteLine(TextResources.ExtractNoPath, path);
                    }
                    entries = entryItems;
                }
                else entries = paths.Select(IndexedString.Selector);
            }
            else entries = entryFile.Values;

            var textEncoding = new UTF8Encoding(false, false);
            foreach (IndexedString i in entries)
            {
                string curPath;
                string curEntry = i.Value;
                if (paths == null)
                    curEntry = Path.GetFullPath(i.Value);

                int nextDex = i.Index + 1;

                if (nextDex <= 0 || nextDex >= ordered.Length || ordered[nextDex] != entryPath)
                    curPath = Path.GetFileName(i.Value);
                else
                    curPath = entryPath.ValuesByIndex[nextDex].Value;

                if (paths != null)
                    curPath = Path.GetFullPath(curPath);
                else
                {
                    curPath = curPath.Trim();
                    {
                        if (textEncoding.GetByteCount(curPath) > byte.MaxValue)
                        {
                            Console.Error.WriteLine(TextResources.PathTooLong, curPath);
                            return true;
                        }

                        if (isSingle)
                        {
                            if (DieFledermausStream.IsValidFilename(curPath))
                            {
                                Console.Error.WriteLine(TextResources.PathInvalid, curPath);
                                continue;
                            }
                        }
                        else if (!DieFledermauZArchive.IsValidFilePath(curPath))
                        {
                            Console.Error.WriteLine(TextResources.PathInvalid, curPath);
                            continue;
                        }
                    }
                }

                string existingPath;
                if (!allFilePaths.TryGetValue(curEntry, out existingPath))
                    allFilePaths.Add(curEntry, curPath);
                else if (existingPath != curPath)
                {
                    Console.Error.WriteLine(TextResources.PathDup, curEntry, existingPath);
                    return true;
                }
            }

            if (allFilePaths.Count == 0)
            {
                Console.Error.WriteLine(TextResources.NoValidEntries);
                return true;
            }
            return false;
        }

        private static string GetTempPath(string archivePath)
        {
            string[] exts = { ".temp", ".tmp" };

            for (int i = 0; i < exts.Length; i++)
            {
                try
                {
                    string getPath = Path.GetFullPath(archivePath + exts[i]);
                    if (!File.Exists(getPath))
                        return getPath;
                }
                catch (PathTooLongException) { }
            }

            for (int i = 0; i < exts.Length; i++)
            {
                try
                {
                    string getPath = Path.GetFullPath(Path.ChangeExtension(archivePath, exts[i]));
                    if (!File.Exists(getPath))
                        return getPath;
                }
                catch (PathTooLongException) { }
            }

            int len = Path.GetFileNameWithoutExtension(archivePath).Length;

            const string alphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789-+!=";

            string dir = Path.GetDirectoryName(archivePath);
            string returnPath;

            Random rng = new Random();

            do
            {
                StringBuilder sb = new StringBuilder();

                for (int i = 0; i < len; i++)
                    sb.Append(alphanumeric[rng.Next(alphanumeric.Length)]);

                returnPath = Path.Combine(dir, sb.ToString());

                for (int i = 0; i < exts.Length; i++)
                {
                    try
                    {
                        returnPath = Path.GetFullPath(returnPath + exts[i]);
                        break;
                    }
                    catch (PathTooLongException) { }
                }
            }
            while (File.Exists(returnPath));

            return returnPath;
        }

        private static BigInteger ReadIndex(ClParamValue cIndex)
        {
            try
            {
                BigInteger index = new BigInteger(cIndex.Value);
                if (index.CompareTo(BigInteger.Zero) < 0)
                    throw new InvalidDataException();

                return index;
            }
            catch
            {
                Console.Error.WriteLine(TextResources.BadInteger, cIndex.Key, cIndex.Value);
                return null;
            }
        }

        private static void VerifySigns(ICipherParameters keyObj, DieFledermauZArchiveEntry curEntry)
        {
            if (keyObj == null || curEntry == null)
                return;

            try
            {
                RsaKeyParameters rsaParam = keyObj as RsaKeyParameters;
                if (rsaParam != null)
                {
                    if (!curEntry.IsRSASigned)
                        return;

                    curEntry.RSASignParameters = rsaParam;
                    if (curEntry.VerifyRSASignature())
                        Console.WriteLine(TextResources.SignRSAVerified, curEntry.Path);
                    else
                        Console.Error.WriteLine(TextResources.SignRSAUnverified, curEntry.Path);
                    return;
                }

                DsaKeyParameters dsaParam = keyObj as DsaKeyParameters;
                if (dsaParam != null)
                {
                    if (!curEntry.IsDSASigned)
                        return;

                    curEntry.DSASignParameters = dsaParam;
                    if (curEntry.VerifyDSASignature())
                        Console.WriteLine(TextResources.SignDSAVerified, curEntry.Path);
                    else
                        Console.Error.WriteLine(TextResources.SignDSAUnverified, curEntry.Path);
                }

                ECKeyParameters ecdsaParam = keyObj as ECKeyParameters;
                if (ecdsaParam == null || !curEntry.IsECDSASigned) //The first one probably shouldn't happen, but ...
                    return;

                curEntry.ECDSASignParameters = ecdsaParam;
                if (curEntry.VerifyECDSASignature())
                    Console.WriteLine(TextResources.SignECDSAVerified, curEntry.Path);
                else
                    Console.Error.WriteLine(TextResources.SignECDSAUnverified, curEntry.Path);
            }
            catch (Exception x)
            {
                Console.Error.WriteLine(x.Message);
            }
        }

        private const string SpinnyChars = "-\\/";
        private static int SpinnyDex = 0;
        private static bool SpinnyGot = false;

        private static void Entry_Progress(object sender, MausProgressEventArgs e)
        {
            DieFledermauZItem item = (DieFledermauZItem)sender;

            if (e.State == MausProgressState.CompletedWriting)
            {
                SpinnyGot = false;
                Console.WriteLine("\b \b");
                return;
            }

            if (SpinnyGot)
                Console.Write("\b");
            else
            {
                SpinnyGot = true;
                Console.Write(item.Path);
                Console.Write(' ');
            }
            Console.Write(SpinnyChars[SpinnyDex]);
            SpinnyDex++;
            SpinnyDex %= 3;

        }

        private static bool DoFailDecrypt(DieFledermauZItem entry, Regex[] matches, RsaKeyParameters rsaKey, ClParamFlag interactive, int i, ref string ssPassword)
        {
            if (entry.Path != null && !MatchesRegexAny(matches, entry.Path))
                return true;
            if (entry.EncryptionFormat == MausEncryptionFormat.None || entry.IsDecrypted)
                return !MatchesRegexAny(matches, entry.Path);

            if (!interactive.IsSet && rsaKey == null)
            {
                Console.Error.WriteLine(TextResources.EncryptedExEntry, GetName(i, entry));
                return true;
            }

            if (ssPassword == null)
            {
                Console.WriteLine(TextResources.EncryptedExEntry, GetName(i, entry));
                return EncryptionPrompt(entry, rsaKey, interactive, out ssPassword) || !MatchesRegexAny(matches, entry.Path);
            }

            try
            {
                entry.Password = ssPassword;

                entry = entry.Decrypt();
                return !MatchesRegexAny(matches, entry.Path);
            }
            catch (CryptoException)
            {
                Console.WriteLine(TextResources.EncryptedExEntry, GetName(i, entry));
                return EncryptionPrompt(entry, rsaKey, interactive, out ssPassword) || !MatchesRegexAny(matches, entry.Decrypt().Path);
            }
        }

        private static string GetName(int i, DieFledermauZItem entry)
        {
            if (entry.Path != null) return entry.Path;

            if (entry.EncryptionFormat == MausEncryptionFormat.None)
                return TextResources.UnnamedFile;

            if (entry is DieFledermauZItemUnknown)
                return string.Format(TextResources.ListEncryptedUnknown, i + 1);

            return string.Format(TextResources.ListEncryptedEntry, i + 1);
        }

        private static bool CreateEncrypted(ClParamEnum<MausEncryptionFormat> cEncFmt, RsaKeyParameters rsaKey, ClParamFlag interactive,
            out MausEncryptionFormat encFormat, out string ssPassword)
        {
            encFormat = MausEncryptionFormat.None;
            if (cEncFmt.IsSet) //Only true if Interactive is also true
            {
                encFormat = cEncFmt.Value.Value;

                if (EncryptionPrompt(null, rsaKey, interactive, out ssPassword))
                    return true;
                return false;
            }
            ssPassword = null;
            return false;
        }

        private static void ShowHelp(ClParam[] clParams, bool showFull)
        {
            Console.Write('\t');
            Console.WriteLine(TextResources.Usage);

            StringBuilder commandName = new StringBuilder();
            if (Type.GetType("Mono.Runtime") != null)
                commandName.Append("mono ");
            commandName.Append(Path.GetFileName(typeof(Program).Assembly.Location));

            Console.WriteLine(TextResources.HelpCompress);
            Console.WriteLine(" > {0} -cf [{1}.maus] {2}1 {2}2 {2}3 ...", commandName, TextResources.HelpArchive, TextResources.HelpInput);
            Console.WriteLine();
            Console.WriteLine(TextResources.HelpDecompress);
            Console.WriteLine(" > {0} -xf [{1}.maus]", commandName, TextResources.HelpArchive);
            Console.WriteLine();
            Console.WriteLine(TextResources.HelpList);
            Console.WriteLine(" > {0} -lvf [{1}.maus]", commandName, TextResources.HelpArchive);
            Console.WriteLine();
            Console.WriteLine(TextResources.HelpHelp);
            Console.WriteLine(" > {0} --help", commandName);
            Console.WriteLine();

            if (showFull)
            {
                Console.Write('\t');
                Console.WriteLine(TextResources.Parameters);

                for (int i = 0; i < clParams.Length; i++)
                {
                    var curParam = clParams[i];

                    IEnumerable<string> paramList;

                    ClParamValueBase cParamValue = curParam as ClParamValueBase;

                    if (curParam.LongNames.Length == 0)
                        paramList = new string[0];
                    else if (cParamValue != null)
                    {
                        paramList = new string[] { string.Concat(curParam.LongNames[0], "=<", cParamValue.ArgName, ">") }.
                            Concat(new ArraySegment<string>(curParam.LongNames, 1, curParam.LongNames.Length - 1));
                    }
                    else paramList = curParam.LongNames;

                    paramList = paramList.Select((n, index) => "--" + n);
                    if (curParam.ShortName != '\0')
                    {
                        string shortName = "-" + curParam.ShortName;

                        if (cParamValue != null)
                            shortName += " <" + cParamValue.ArgName + ">";

                        paramList = new string[] { shortName }.Concat(paramList);
                    }

                    Console.WriteLine(string.Join(", ", paramList));

                    const string indent = "   ";
                    string[] helpMessages = curParam.HelpMessage.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j < helpMessages.Length; j++)
                    {
                        Console.Write(indent);
                        Console.WriteLine(helpMessages[j]);
                    }
                    if (curParam == curParam.Parser.RawParam)
                    {
                        Console.Write(indent);
                        Console.WriteLine(TextResources.ParamRaw, curParam.ShortName == '\0' ? "--" + curParam.LongNames[0] : "-" + curParam.ShortName);
                    }
                    Console.WriteLine();
                }
            }
        }

#if DEBUG
        internal static void GoThrow(Exception e)
        {
            Console.Error.WriteLine(e.GetType().ToString() + ":");
            Console.Error.WriteLine(e.ToString());
            Console.Error.WriteLine("Throw? Y/N> ");
            var key = Console.ReadKey().Key;
            Console.WriteLine();
            if (key == ConsoleKey.Y)
                throw new Exception(e.Message, e);
        }
#endif
        private static Regex GetRegex(IndexedString s)
        {
            return new Regex("^" + Regex.Escape(s.Value).Replace("\\*", ".*").Replace("\\?", "."));
        }

        private static bool MatchesRegexAny(IEnumerable<Regex> regex, string path)
        {
            if (path == null || regex == null)
                return true;
            foreach (Regex curRegex in regex)
            {
                if (curRegex.IsMatch(path))
                    return true;
            }
            return false;
        }

        private static bool EncryptionPrompt(IMausCrypt ds, RsaKeyParameters rsaKey, ClParamFlag interactive, out string ss)
        {
            if (rsaKey != null)
            {
                ss = null;
                if (ds == null)
                    return false;

                if (ds.IsRSAEncrypted)
                {
                    ds.RSAEncryptParameters = rsaKey;
                    try
                    {
                        ds.Decrypt();
                    }
                    catch (CryptoException x)
                    {
                        Console.Error.WriteLine(x.Message);
                        return true;
                    }
                    return false;
                }
            }

            bool notFound1 = true;
            ss = null;
            do
            {
                try
                {
                    ss = new string(new PasswordLoader(interactive).GetPassword());
                }
                catch (PasswordCancelledException)
                {
                    return true;
                }

                if (ds == null)
                {
                    Console.WriteLine(TextResources.KeepSecret);
                    notFound1 = false;
                }
                else
                {
                    try
                    {
                        ds.Key = Hex.Decode(Encoding.UTF8.GetBytes(ss));
                        ds.Decrypt();
                        return false;
                    }
                    catch
                    {
                        ds.Key = null;
                    }
                    try
                    {
                        ds.Password = ss;
                        ds.Decrypt();
                        notFound1 = false;
                    }
                    catch (CryptoException)
                    {
                        Console.Error.WriteLine(TextResources.EncryptedBadKey);
                        continue;
                    }
                }
            }
            while (notFound1);
            return false;
        }

        private static string NoOutputCreate(ClParam param)
        {
            return string.Format(TextResources.NoOutputCreate, param.Key);
        }

        private static string NoEntryExtract(ClParam param)
        {
            return string.Format(TextResources.NoEntryExtract, param.Key);
        }

        private static bool OverwritePrompt(ClParamFlag interactive, ClParamFlag overwrite, ClParamFlag skipexist, ClParamFlag verbose, ref string filename)
        {
            if (!File.Exists(filename))
                return false;

            if (verbose.IsSet || skipexist.IsSet || interactive.IsSet)
            {
                if (!skipexist.IsSet || interactive.IsSet)
                    Console.WriteLine(TextResources.OverwriteAlert, filename);
                else
                    Console.Error.WriteLine(TextResources.OverwriteAlert, filename);
            }

            if (skipexist.IsSet || (!overwrite.IsSet && interactive.IsSet && OverwritePrompt(ref filename)))
            {
                Console.WriteLine(TextResources.OverwriteSkip);
                return true;
            }

            return false;
        }

        private static int Return(int value, ClParamFlag interactive)
        {
            if (interactive.IsSet)
            {
                Console.WriteLine(TextResources.AnyKey);
                Console.ReadKey();
            }
            return value;
        }

        private static bool OverwritePrompt(ref string filename)
        {
            bool notFound = true;
            do
            {
                Console.Write(TextResources.OverwritePrompt + "> ");

                string line = Console.ReadLine().Trim();
                const string oYes = "yes", oNo = "no", oRen = "rename";

                if (string.IsNullOrEmpty(line))
                    continue;

                if (oYes.StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(oYes, StringComparison.OrdinalIgnoreCase) ||
                    TextResources.OverYes.StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(TextResources.OverNo, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(TextResources.Overwrite);
                    notFound = false;
                }
                else if (oNo.StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(oNo, StringComparison.OrdinalIgnoreCase) ||
                    TextResources.OverNo.StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(TextResources.OverNo, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                else if (oRen.StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(oRen, StringComparison.OrdinalIgnoreCase) ||
                    TextResources.OverRename.StartsWith(line, StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(TextResources.OverRename, StringComparison.OrdinalIgnoreCase))
                {
                    string ext = Path.GetExtension(filename);
                    string newPath = string.IsNullOrEmpty(ext) ? filename + ".out" : Path.ChangeExtension(filename, ".out" + ext);
                    if (!File.Exists(newPath))
                    {
                        filename = newPath;
                        Console.WriteLine(TextResources.OverwriteRename, newPath);
                        return false;
                    }

                    checked
                    {
                        for (ulong i = 1; i != 0; i++)
                        {
                            string longText = i.ToString(NumberFormatInfo.InvariantInfo);

                            string longPath = string.IsNullOrEmpty(ext) ? string.Concat(filename, ".out", longText) :
                                Path.ChangeExtension(filename, string.Concat(filename, ".out", longText, ext));

                            if (!File.Exists(longPath))
                            {
                                Console.WriteLine(TextResources.OverwriteRename, longPath);
                                filename = longPath;
                                return false;
                            }
                        }
                    }
                }
            }
            while (notFound);
            return false;
        }
    }

    internal class PasswordLoader : IPasswordFinder
    {
        private ClParam _interactive;

        public PasswordLoader(ClParamFlag interactive, string path, bool encryption)
        {
            _interactive = interactive;
            _path = path;
            _enc = encryption;
        }

        public PasswordLoader(ClParamFlag interactive)
            : this(interactive, null, true)
        {
        }

        private string _path;
        public string Path
        {
            get { return _path; }
            set { _path = value; }
        }

        private bool _enc;
        public bool Encryption
        {
            get { return _enc; }
            set { _enc = value; }
        }

        public char[] GetPassword()
        {
            if (!_interactive.IsSet)
                throw new InvalidDataException(TextResources.NoPassword);

            List<char> charList = new List<char>();
            if (!string.IsNullOrWhiteSpace(_path))
            {
                Console.WriteLine(_enc ? TextResources.KeyFileEnc : TextResources.KeyFileSig, _path);
                _path = null;
            }
            Console.WriteLine(TextResources.PasswordPrompt);
            Console.Write("> ");

            ConsoleKeyInfo c;

            while ((c = Console.ReadKey()).Key != ConsoleKey.Enter)
            {
                if (c.Key == ConsoleKey.Backspace)
                {
                    charList.RemoveAt(charList.Count - 1);
                    Console.Write(" ");
                }
                else
                {
                    Console.Write("\b \b");
                    charList.Add(c.KeyChar);
                }
            }
            Console.WriteLine(">");

            if (charList.Count == 0)
                throw new PasswordCancelledException();

            return charList.ToArray();
        }
    }

    [Serializable]
    internal class PasswordCancelledException : Exception
    {
    }
}
