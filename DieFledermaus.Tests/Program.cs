﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace DieFledermaus.Tests
{
    internal static class Program
    {
        const int bigBufferLength = 70000;
        [STAThread]
        static void Main()
        {
            byte[] bigBuffer = new byte[bigBufferLength];
            for (int i = 0; i < bigBufferLength; i++)
                bigBuffer[i] = (byte)(i + 1);

            using (MemoryStream ms = new MemoryStream())
            {
                RsaKeyParameters publicKeySig, privateKeySig, publicKeyEnc, privateKeyEnc;

                {
                    const string xmlSig = "<RSAKeyValue><!-- Not suitable for use outside the test-app. --><Modulus>2/xGqjup0HUXsCipNDvXX4Y" +
                        "L0cyc5CK5a5ksyPk6AGFHz4nGneGA72WBBodtziI+cBWujChTQyMMNiQ+h0JOvFYnvMB8u4bkOPrf2rqscA/04nDodkXRqXoIlyHGp0VOBGfA5f4UU" +
                        "AMZzLwUrLkBpdUwv3e3mQ2jDAz5+eva2cs=</Modulus><Exponent>AQAB</Exponent><P>6WoHJlCCQp8kQGqN+/b73E6nS/x/yYX1kW/xlqDPp" +
                        "MtPcbUlq5YNqL3+qQT9ynoFHjbnFYfTiupKmillojmKxQ==</P><Q>8UWZ8AvsKkV9QAYxYr6lAtuyEn0Vvqo4xX9rglNKpYIrdhPcr3DnMb8ekIGm" +
                        "z4KvIy74bbhIUJLeaZa3OQFbTw==</Q><DP>ZAibzdDdMp4vlCfWd/DO2gkfa9JoFb8CknUObca3luHHR20iGtpxOitLE7be6cLHpL5U5QZUJAnrNQ" +
                        "ye0RqmHQ==</DP><DQ>dgJcG+xI9BgO/hzJVQn4feBlRePGmf56TCdZt2Hz9eYoSdXHMEyh2FQpp/ayV3cNIMFdo5TqUfa0MKMWNRyzww==</DQ><I" +
                        "nverseQ>23Cnyug4M7ws64AFAcQ1H3sgEWEhCaYH3YQ2Aesv84Cf5cDZMtmVCq0paPx7uDGgl4eZX1jGXyKDql/JSXalbw==</InverseQ><D>Pisw" +
                        "aUGNPx0oQZ9sGhfjSNqgEn1pxUtO7WqPboiIbL0RR0SffdTR1FXyPb8eOAgTbyeheXiX9zw7Yj2h8iW6DBeCnQbaNA/p3Iw8RBYcTPdz9YLhFOBid2" +
                        "M26OC/aBozT6h7jLhyuNVkWnFh5Een5QGobtwuvJHkhSIBWUkuevk=</D></RSAKeyValue>";

                    var keyPair = GetKeyPair(xmlSig);
                    publicKeySig = (RsaKeyParameters)keyPair.Public;
                    privateKeySig = (RsaKeyParameters)keyPair.Private;

                    const string xmlEnc = "<RSAKeyValue><!-- Not suitable for use outside the test-app. --><Modulus>tLxR5IiTcQ93LArqHYeC4o5" +
                        "JTBavu6z1G1F9LG2/q/cqZql6bNHzK6XGVYfEVxgl2Lxes3g+4xFcUvC0qPifkyEN8F4l8gmIdtR4KAT+8aSH7xdgVIXexIWDXlk7DfAKHo3KVTy7j" +
                        "7ZF1596TsGATGrNx3UWH/x/9bR5ltuLfbU=</Modulus><Exponent>AQAB</Exponent><P>zdUnz0+Nh4T6rjTXfoyxCBjCOjoajgfZLO0iw20dD" +
                        "PlQpUXq5Wp+LSDwjSZCyIJPXkdSLtT19lmxq0xNOgvYwQ==</P><Q>4Mk/DjUo0NEwoHUmJdVG/2qdfWQewI7v8gJKo/GfaVEAPp7urkye4eTuIJz/" +
                        "ckNef296Emi9cI+ZFmVgY8NN9Q==</Q><DP>dAb3JO6UOlNkt/TDkOugE49ZVVdRhsS30JJwKTeFy71yj2fFTMNmEuxhjT+HH94M/Xk4w3t6lv7in0" +
                        "wosFLjQQ==</DP><DQ>bgZTFNM0TTF3SbLNn0sLW02GFLAC1WGhVKWGf0RvMI9zPTNxxGLAifUSEWiHKBiNknawG36k6wl+dxXb3jjkWQ==</DQ><I" +
                        "nverseQ>k/TWXiewe45/3BmL1BPr6uLBPfQ4sYGFcOwXRhKiLDaE6d3VcVXoIb58DH5BHSMtLMkwOBo8mfaCFiKsV0V0xA==</InverseQ><D>Kg1B" +
                        "HqPabm1zRHebplg/z1fc3QvQQqII+5y3u60jci8VmgJn3kbxReAR6BepSrxvHeEiRa6+LxX8fb3Mwx3p/qz6H6uimCFv+tt8ryaAj6GFjJhysONpZ6" +
                        "ij1mVn5jsmMgOMRfv68XRRsaoH9a2pYTeht7wF6KnbfxXzBqVbG4E=</D></RSAKeyValue>";

                    keyPair = GetKeyPair(xmlEnc);

                    publicKeyEnc = (RsaKeyParameters)keyPair.Public;
                    privateKeyEnc = (RsaKeyParameters)keyPair.Private;
                }

                Console.WriteLine("Creating archive ...");
                Stopwatch sw;
                using (DieFledermauZArchive archive = new DieFledermauZArchive(ms, MauZArchiveMode.Create, true))
                {
                    SetEntry(archive, bigBuffer, MausCompressionFormat.Deflate, MausEncryptionFormat.None, privateKeySig);
                    SetEntry(archive, bigBuffer, MausCompressionFormat.Lzma, MausEncryptionFormat.Aes, privateKeySig);
                    SetEntry(archive, bigBuffer, MausCompressionFormat.None, MausEncryptionFormat.Threefish, privateKeySig);
                    Console.WriteLine(" - Building empty directory: EmptyDir/");
                    var emptyDir = archive.AddEmptyDirectory("EmptyDir/", MausEncryptionFormat.Twofish);
                    SetPasswd(emptyDir);
                    Console.WriteLine("Compressing archive ...");
                    sw = Stopwatch.StartNew();
                }

                sw.Stop();
                Console.WriteLine("Completed compression in {0}ms", sw.Elapsed.TotalMilliseconds);
                Console.WriteLine();
                Console.WriteLine("Decoding archive ...");

                using (DieFledermauZArchive archive = new DieFledermauZArchive(ms, MauZArchiveMode.Read, true))
                {
                    foreach (DieFledermauZItem item in archive.Entries.ToArray().Where(i => i.EncryptionFormat != MausEncryptionFormat.None))
                    {
                        Console.WriteLine(" - Decrypting file ...");
                        SetPasswd(item);
                        sw = Stopwatch.StartNew();
                        var dItem = item.Decrypt();
                        sw.Stop();
                        Console.WriteLine("Successfully decrypted {0} in {1}ms", dItem.Path, sw.Elapsed.TotalMilliseconds);
                    }

                    foreach (DieFledermauZArchiveEntry entry in archive.Entries.OfType<DieFledermauZArchiveEntry>())
                    {
                        entry.RSASignParameters = publicKeySig;
                        entry.VerifyRSASignature();

                        Console.WriteLine(" - Reading file: " + entry.Path);
                        Console.WriteLine("Hash: " + GetString(entry.ComputeHash()));
                        if (entry.IsRSASignVerified)
                            Console.WriteLine("RSA signature is verified.");
                        else
                            Console.WriteLine("RSA signature is NOT verified!");
                    }
                }
            }

            Console.WriteLine("Press any key to continue ...");
            Console.ReadKey();
        }

        private static AsymmetricCipherKeyPair GetKeyPair(string xml)
        {
            XElement root = XDocument.Parse(xml).Root;

            RsaKeyParameters publicKey = new RsaKeyParameters(false, root.GetBigInt("Modulus"), root.GetBigInt("Exponent"));
            RsaKeyParameters privateKey = new RsaPrivateCrtKeyParameters(publicKey.Modulus, publicKey.Exponent, root.GetBigInt("D"),
                  root.GetBigInt("P"), root.GetBigInt("Q"), root.GetBigInt("DP"), root.GetBigInt("DQ"), root.GetBigInt("InverseQ"));

            return new AsymmetricCipherKeyPair(publicKey, privateKey);
        }

        public static BigInteger GetBigInt(this XElement node, XName name)
        {
            return new BigInteger(1, Convert.FromBase64String(node.Element(name).Value));
        }

        private static void SetEntry(DieFledermauZArchive archive, byte[] bigBuffer, MausCompressionFormat compFormat, MausEncryptionFormat encFormat,
            RsaKeyParameters privateKeySig)
        {
            var entry = archive.Create("Files/" + compFormat.ToString() + encFormat.ToString() + ".dat", compFormat, encFormat);

            entry.RSASignParameters = privateKeySig;

            Console.WriteLine(" - Building file: " + entry.Path);

            if (encFormat != MausEncryptionFormat.None)
            {
                SetPasswd(entry);
            }

            using (Stream stream = entry.OpenWrite())
                stream.Write(bigBuffer, 0, bigBufferLength);

            Console.WriteLine("Hash: " + GetString(entry.ComputeHash()));
        }

        private static void SetPasswd(DieFledermauZItem item)
        {
            const string passwd = "Correct Horse!Battery#Staple69105";

            item.Password = passwd;

            DieFledermauZArchiveEntry entry = item as DieFledermauZArchiveEntry;
            if (entry != null && entry.EncryptedOptions != null && !entry.EncryptedOptions.IsReadOnly)
                entry.EncryptedOptions.AddAll();
        }

        private static string GetString(byte[] hash)
        {
            return string.Concat(hash.Select(i => i.ToString("x2", System.Globalization.NumberFormatInfo.InvariantInfo)).ToArray());
        }
    }
}
