﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TrustExec.Interop;

namespace TrustExec.Library
{
    internal class Helpers
    {
        public static int AddSidMapping(
            string domain,
            string username,
            IntPtr pSid)
        {
            int ntstatus;
            var input = new LSA_SID_NAME_MAPPING_OPERATION_ADD_INPUT();

            if (string.IsNullOrEmpty(domain) || pSid == IntPtr.Zero)
                return -1;

            input.DomainName = new UNICODE_STRING(domain);

            if (username != null)
                input.AccountName = new UNICODE_STRING(username);

            input.Sid = pSid;

            ntstatus = NativeMethods.LsaManageSidNameMapping(
                LSA_SID_NAME_MAPPING_OPERATION_TYPE.LsaSidNameMappingOperation_Add,
                input,
                out IntPtr output);

            if (pSid != IntPtr.Zero)
                NativeMethods.LocalFree(pSid);

            if (output != IntPtr.Zero)
                NativeMethods.LsaFreeMemory(output);

            return ntstatus;
        }


        public static bool CompareIgnoreCase(string strA, string strB)
        {
            return (string.Compare(strA, strB, StringComparison.OrdinalIgnoreCase) == 0);
        }


        public static string ConvertAccountNameToSidString(
            ref string accountName,
            out SID_NAME_USE peUse)
        {
            int error;
            bool status;
            string username;
            string domain;
            Regex rx1 = new Regex(
                @"^[^\\]+\\[^\\]+$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Regex rx2 = new Regex(
                @"^[^\\]+$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            IntPtr pSid;
            int cbSid = 8;
            StringBuilder referencedDomainName = new StringBuilder();
            int cchReferencedDomainName = 8;
            peUse = 0;

            if (rx1.IsMatch(accountName))
            {
                try
                {
                    domain = accountName.Split('\\')[0];
                    username = accountName.Split('\\')[1];

                    if (domain == ".")
                    {
                        accountName = string.Format(
                            @"{0}\{1}",
                            Environment.MachineName,
                            username);
                    }
                }
                catch
                {
                    return null;
                }
            }
            else if (!rx2.IsMatch(accountName))
            {
                return null;
            }

            do
            {
                referencedDomainName.Capacity = cchReferencedDomainName;
                pSid = Marshal.AllocHGlobal(cbSid);
                ZeroMemory(pSid, cbSid);

                status = NativeMethods.LookupAccountName(
                    null,
                    accountName,
                    pSid,
                    ref cbSid,
                    referencedDomainName,
                    ref cchReferencedDomainName,
                    out peUse);
                error = Marshal.GetLastWin32Error();

                if (!status)
                {
                    referencedDomainName.Clear();
                    Marshal.FreeHGlobal(pSid);
                }
            } while (!status && error == Win32Consts.ERROR_INSUFFICIENT_BUFFER);

            if (!status)
                return null;

            if (!NativeMethods.IsValidSid(pSid))
                return null;

            accountName = ConvertSidToAccountName(pSid, out peUse);

            if (NativeMethods.ConvertSidToStringSid(pSid, out string strSid))
            {
                NativeMethods.LocalFree(pSid);

                return strSid;
            }
            else
            {
                NativeMethods.LocalFree(pSid);

                return null;
            }
        }


        public static string[] ParseGroupSids(string extraSidsString)
        {
            var result = new List<string>();
            var sidArray = extraSidsString.Split(',');
            var regexSid = new Regex(
                @"^S-1(-\d+)+$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
            string accountName;
            string sid;

            Console.WriteLine("[>] Parsing group SID(s).");

            for (var idx = 0; idx < sidArray.Length; idx++)
            {
                sid = sidArray[idx].Trim();

                if (!regexSid.IsMatch(sid))
                {
                    Console.WriteLine("[!] {0} is invalid format. Ignored.", sid);
                    continue;
                }

                accountName = ConvertSidStringToAccountName(
                    ref sid,
                    out SID_NAME_USE peUse);

                if (peUse == SID_NAME_USE.SidTypeAlias ||
                    peUse == SID_NAME_USE.SidTypeWellKnownGroup)
                {
                    result.Add(sid);
                    Console.WriteLine("[+] \"{0}\" is added as an extra group.", accountName);
                    Console.WriteLine("    |-> SID  : {0}", sid);
                    Console.WriteLine("    |-> Type : {0}", peUse);
                }
                else
                {
                    Console.WriteLine("[-] \"{0}\" is not group account. Ignored.", accountName);
                    Console.WriteLine("    |-> SID  : {0}", sid);
                    Console.WriteLine("    |-> Type : {0}", peUse);
                }
            }

            return result.ToArray();
        }


        public static string ConvertSidStringToAccountName(
            ref string sid,
            out SID_NAME_USE peUse)
        {
            string accountName;
            sid = sid.ToUpper();

            if (!NativeMethods.ConvertStringSidToSid(sid, out IntPtr pSid))
            {
                peUse = 0;
                return null;
            }

            accountName = ConvertSidToAccountName(pSid, out peUse);
            NativeMethods.LocalFree(pSid);

            return accountName;
        }


        public static string ConvertSidToAccountName(
            IntPtr pSid,
            out SID_NAME_USE peUse)
        {
            bool status;
            int error;
            StringBuilder pName = new StringBuilder();
            int cchName = 4;
            StringBuilder pReferencedDomainName = new StringBuilder();
            int cchReferencedDomainName = 4;

            do
            {
                pName.Capacity = cchName;
                pReferencedDomainName.Capacity = cchReferencedDomainName;

                status = NativeMethods.LookupAccountSid(
                    null,
                    pSid,
                    pName,
                    ref cchName,
                    pReferencedDomainName,
                    ref cchReferencedDomainName,
                    out peUse);
                error = Marshal.GetLastWin32Error();

                if (!status)
                {
                    pName.Clear();
                    pReferencedDomainName.Clear();
                }
            } while (!status && error == Win32Consts.ERROR_INSUFFICIENT_BUFFER);

            if (!status)
                return null;

            if (peUse == SID_NAME_USE.SidTypeDomain)
            {
                return pReferencedDomainName.ToString();
            }
            else if (cchName == 0)
            {
                return pReferencedDomainName.ToString();
            }
            else if (cchReferencedDomainName == 0)
            {
                return pName.ToString();
            }
            else
            {
                return string.Format("{0}\\{1}",
                    pReferencedDomainName.ToString(),
                    pName.ToString());
            }
        }


        public static IntPtr GetInformationFromToken(
            IntPtr hToken,
            TOKEN_INFORMATION_CLASS tokenInfoClass)
        {
            bool status;
            int error;
            int length = 4;
            IntPtr buffer;

            do
            {
                buffer = Marshal.AllocHGlobal(length);
                ZeroMemory(buffer, length);
                status = NativeMethods.GetTokenInformation(
                    hToken, tokenInfoClass, buffer, length, out length);
                error = Marshal.GetLastWin32Error();

                if (!status)
                    Marshal.FreeHGlobal(buffer);
            } while (!status && (error == Win32Consts.ERROR_INSUFFICIENT_BUFFER || error == Win32Consts.ERROR_BAD_LENGTH));

            if (!status)
                return IntPtr.Zero;

            return buffer;
        }


        public static bool GetPrivilegeLuid(
            string privilegeName,
            out LUID luid)
        {
            int error;

            if (!NativeMethods.LookupPrivilegeValue(
                null,
                privilegeName,
                out luid))
            {
                error = Marshal.GetLastWin32Error();
                Console.WriteLine("[-] Failed to lookup {0}.", privilegeName);
                Console.WriteLine("    |-> {0}\n", GetWin32ErrorMessage(error, false));

                return false;
            }

            return true;
        }


        public static string GetPrivilegeName(LUID priv)
        {
            int error;
            int cchName = 255;
            StringBuilder privilegeName = new StringBuilder(255);

            if (!NativeMethods.LookupPrivilegeName(
                null,
                ref priv,
                privilegeName,
                ref cchName))
            {
                error = Marshal.GetLastWin32Error();
                Console.WriteLine("[-] Failed to lookup privilege name.");
                Console.WriteLine("    |-> {0}\n", GetWin32ErrorMessage(error, false));
                
                return null;
            }

            return privilegeName.ToString();
        }


        public static string GetWin32ErrorMessage(int code, bool isNtStatus)
        {
            int nReturnedLength;
            int nSizeMesssage = 256;
            var message = new StringBuilder(nSizeMesssage);
            var dwFlags = FormatMessageFlags.FORMAT_MESSAGE_FROM_SYSTEM;
            var pNtdll = IntPtr.Zero;

            if (isNtStatus)
            {
                foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                {
                    if (CompareIgnoreCase(Path.GetFileName(module.FileName), "ntdll.dll"))
                    {
                        pNtdll = module.BaseAddress;
                        dwFlags |= FormatMessageFlags.FORMAT_MESSAGE_FROM_HMODULE;
                        break;
                    }
                }
            }

            nReturnedLength = NativeMethods.FormatMessage(
                dwFlags,
                pNtdll,
                code,
                0,
                message,
                nSizeMesssage,
                IntPtr.Zero);

            if (nReturnedLength == 0)
                return string.Format("[ERROR] Code 0x{0}", code.ToString("X8"));
            else
                return string.Format("[ERROR] Code 0x{0} : {1}", code.ToString("X8"), message.ToString().Trim());
        }


        public static int RemoveSidMapping(
            string domain,
            string username)
        {
            int ntstatus;
            var input = new LSA_SID_NAME_MAPPING_OPERATION_REMOVE_INPUT();

            if (string.IsNullOrEmpty(domain))
                return -1;

            input.DomainName = new UNICODE_STRING(domain);

            if (username != null)
                input.AccountName = new UNICODE_STRING(username);

            ntstatus = NativeMethods.LsaManageSidNameMapping(
                LSA_SID_NAME_MAPPING_OPERATION_TYPE.LsaSidNameMappingOperation_Remove,
                input,
                out IntPtr output);

            if (output != IntPtr.Zero)
                NativeMethods.LsaFreeMemory(output);

            return ntstatus;
        }


        public static void ZeroMemory(IntPtr buffer, int size)
        {
            var nullBytes = new byte[size];
            Marshal.Copy(nullBytes, 0, buffer, size);
        }
    }
}
