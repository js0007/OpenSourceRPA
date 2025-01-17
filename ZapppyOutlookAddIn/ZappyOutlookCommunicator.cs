﻿using Microsoft.Office.Interop.Outlook;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.Threading;
using ZappyMessages;
using ZappyMessages.Helpers;
using ZappyMessages.Logger;
using ZappyMessages.OutlookMessages;
using ZappyMessages.PubSub;
using ZappyMessages.PubSubHelper;
using Exception = System.Exception;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ZappyOutlookAddIn
{
    internal class ZappyOutlookCommunicator : IOutlookZappyTaskCommunication
    {
        private PubSubClient _Client;
        private Outlook.NameSpace outlookNameSpace;
        private Outlook.MAPIFolder inbox;
        private Outlook.Items items;
        private Application application;
        private List<OutlookNewEmailTriggerInfo> _newMailTriggers;

        public ZappyOutlookCommunicator()
        {
            _newMailTriggers = new List<OutlookNewEmailTriggerInfo>();

            if (ThisAddIn.Instance == null || ThisAddIn.Instance.Application == null)
            {
                throw new InvalidOperationException();
            }

            ConsoleLogger.Info("Plugin Loaded");

            //add response channels
            EndpointAddress _RemoteAddress = new EndpointAddress(ZappyMessagingConstants.EndpointLocationZappyService);
            _Client = new PubSubClient("PlaybackHelper", _RemoteAddress,
                new int[] { PubSubTopicRegister.ZappyOutlookRequest, PubSubTopicRegister.ZappyPlaybackHelper2OutlookRequest });

            //NOT receiving messages - investigate
            _Client.DataPublished += _Client_DataPublished;
            // Cache the Excel application of this addin.
            this.application = ThisAddIn.Instance.Application;

            outlookNameSpace = this.application.GetNamespace("MAPI");

            inbox = outlookNameSpace.GetDefaultFolder(
                Outlook.
                    OlDefaultFolders.olFolderInbox);

            items = inbox.Items;

            items.ItemAdd += Items_ItemAdd;
        }

        internal int GetResponseChannel(int RequestChannel)
        {
            return RequestChannel + 1;
        }

        private void _Client_DataPublished(PubSubClient client, int arg1, string arg2)
        {
            //arg2 = StringCipher.Decrypt(arg2, ZappyMessagingConstants.MessageKey);

            //{message:"GetElementFromPoint"; param1:"156"; param2:"150"}
            //

            Tuple<int, OutlookRequest, string> _Request = ZappySerializer.DeserializeObject<Tuple<int, OutlookRequest, string>>(arg2);
            string _result = null;
            bool _RedoRequired = false;

        REDO:

            try
            {
                switch (_Request.Item2)
                {
                    //case ExcelRequest.SendKeys:
                    //    application.SendKeys(_Request.Item3, true);
                    //    break;

                    case OutlookRequest.SearchEmail:
                        {
                            OutlookMessageInfo _PropertyRequest = ZappySerializer.DeserializeObject<OutlookMessageInfo>(_Request.Item3);
                            _result = ZappySerializer.SerializeObject(new Tuple<int, object>(_Request.Item1, SearchMessage(_PropertyRequest)));
                        }
                        break;

                    case OutlookRequest.SearchEmail_OutlookSearch:
                        {
                            OutlookMessageInfo _PropertyRequest = ZappySerializer.DeserializeObject<OutlookMessageInfo>(_Request.Item3);
                            _result = ZappySerializer.SerializeObject(new Tuple<int, object>(_Request.Item1, SearchMessage_OutlookSearch(_PropertyRequest)));
                        }
                        break;

                    case OutlookRequest.NotifyNewMails:
                        {
                            OutlookNewEmailTriggerInfo activeTrigger = ZappySerializer.DeserializeObject<OutlookNewEmailTriggerInfo>(_Request.Item3);
                            _result = ZappySerializer.SerializeObject(new Tuple<int, object>(_Request.Item1, RegisterNewEmailTriggerOutlook(activeTrigger)));
                        }
                        break;

                    //case OutlookRequest.MoveMail:
                    //    OutlookMessageInfo _PropertyRequest3 = ZappySerializer.DeserializeObject<OutlookMessageInfo>(_Request.Item3);
                    //    _result = ZappySerializer.SerializeObject(new Tuple<int, object>(_Request.Item1, SearchMessage(_PropertyRequest3)));
                    //    break;
                    default:
                        break;
                }
            }
            catch (System.Runtime.InteropServices.COMException cex)
            {
                if ((uint)cex.ErrorCode == 0x800AC472)
                    _RedoRequired = true;
                else
                {
                    ConsoleLogger.Error(cex);
                    throw;
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.Error(ex);
            }

            if (_RedoRequired)
            {
                Thread.Sleep(100);
                goto REDO;

            }

            if (!string.IsNullOrEmpty(_result))
                _Client.Publish(GetResponseChannel(arg1), _result);
        }

        private void HandleNewMailRequest(List<OutlookNewEmailTriggerInfo> activeTriggers)
        {
            //Update the active triggers list
            lock (_newMailTriggers)
            {
                _newMailTriggers.Clear();
                _newMailTriggers.AddRange(activeTriggers);
            }
        }


        public string RegisterNewEmailTriggerOutlook(OutlookNewEmailTriggerInfo activeTrigger)
        {
            OutlookNewEmailTriggerInfo itemtoRemove = null;
            foreach (OutlookNewEmailTriggerInfo _registeredMailTrigger in _newMailTriggers)
            {
                if (_registeredMailTrigger.TriggerId == activeTrigger.TriggerId)
                    itemtoRemove = _registeredMailTrigger;
            }

            if (itemtoRemove != null)
                _newMailTriggers.Remove(itemtoRemove);

            if (!activeTrigger.RemoveTrigger)
                _newMailTriggers.Add(activeTrigger);

            return "Done";
        }

        private void Items_ItemAdd(object Item)
        {
            //string filter = "USED CARS";
            if (Item == null)
                return;

            Outlook.MailItem mail = (Outlook.MailItem)Item;
            //ConsoleLogger.Info(mail.Sender.ToString() + " \n " + mail.SenderEmailAddress + " \n " + mail.Sender.Name);

            if (mail.MessageClass != "IPM.Note")// &&                    mail.Subject.ToUpper().Contains(filter.ToUpper())
                return;

            List<OutlookNewEmailTriggerInfo> matchedTriggers;
            matchedTriggers = new List<OutlookNewEmailTriggerInfo>();

            lock (_newMailTriggers)
            {
                //First find all the matches
                foreach (var item in _newMailTriggers)
                {
                    //If we found a match for our match for any of the triggers then notify server
                    if (CompareEmail(mail, item))
                    {
                        if (!matchedTriggers.Contains(item))
                        {
                            OutlookNewEmailTriggerInfo newInfo = new OutlookNewEmailTriggerInfo()
                            {
                                ParentTaskId = item.ParentTaskId,
                                TriggerId = item.TriggerId,
                                From = GetReadableSourceAddress(mail),
                                To = mail.To,
                                Subject = mail.Subject,
                                Body = mail.Body
                            };

                            //item.From = mail.SenderEmailAddress;
                            //item.To = mail.To;
                            //item.Subject = mail.Subject;
                            //item.Body = mail.Body;
                            if (!string.IsNullOrEmpty(item.SaveDirPath))
                            {
                                string subj = mail.Subject;
                                subj = GenerateUniqueFileWithSubject(subj);
                                string fileName = Path.Combine(item.SaveDirPath, subj + ".msg");
                                mail.SaveAs(fileName);
                            }

                            matchedTriggers.Add(newInfo);
                        }
                    }
                }

                //Raise notify PlaybackHelper service of all the matched triggers
                foreach (var item in matchedTriggers)
                {
                    _Client.Publish(PubSubTopicRegister.Outlook2ZappyPlaybackHelperResponse, ZappySerializer
                            .SerializeObject(new Tuple<OutlookRequest, OutlookNewEmailTriggerInfo>(OutlookRequest.NotifyNewMails, item)));
                }
            }

            //string _FileName = Path.Combine(ZappyMessagingConstants.OutlookFolder, string.Format("_Zappy_{0}_{1}", DateTime.Today.ToString("yyyyMMdd"), Guid.NewGuid().ToString()));
            //mail.SaveAs(_FileName);
            //mail.Move(outlookNameSpace.GetDefaultFolder(
            //    Microsoft.Office.Interop.Outlook.
            //        OlDefaultFolders.olFolderJunk));
        }

        //if email not in outlook and is on server - enable search on server emails as well??
        public string SearchMessage(OutlookMessageInfo outlookMessageInfo)
        {
            inbox = GetRequestedInbox(outlookMessageInfo.OutlookAccountUserName);

            Outlook.Items items = inbox.Items;
            int length = items.Count;
            items.Sort("[ReceivedTime]", true);
            //MailItem item in items
            //Index are 1 based
            for (int i = 1; i <= length; i++)
            {
                if (items[i] is MailItem item)
                {
                    if (CompareEmail(item, outlookMessageInfo))
                    {
                        String filename = item.Subject;
                        filename = GenerateUniqueFileWithSubject(filename);
                        string _FileName = Path.Combine(outlookMessageInfo.SaveMatchedEmailsDirectory, filename + ".msg");
                        item.SaveAs(_FileName);
                        if (outlookMessageInfo.OnlySaveLatest)
                        {
                            break;
                        }
                    }
                    Marshal.ReleaseComObject(item);
                }
            }
            return "done";
        }

        private string GenerateUniqueFileWithSubject(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                return Guid.NewGuid().ToString();
            filename = filename.Substring(0, Math.Min(filename.Length, 10));
            string invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            foreach (char c in invalid)
            {
                filename = filename.Replace(c.ToString(), "");
            }
            filename = filename + " " + Guid.NewGuid().ToString();
            return filename;
        }

        public string SearchMessage_OutlookSearch(OutlookMessageInfo outlookMessageInfo)
        {
            inbox = GetRequestedInbox(outlookMessageInfo.OutlookAccountUserName);

            var matchedItems = SearchFolder(inbox, FixInvalidFilterString(outlookMessageInfo));

            foreach (var item in matchedItems)
            {
                string filename = item.Subject;
                filename = GenerateUniqueFileWithSubject(filename);


                string _FileName = Path.Combine(outlookMessageInfo.SaveMatchedEmailsDirectory, filename + ".msg");
                item.SaveAs(_FileName);

                if (outlookMessageInfo.OnlySaveLatest)
                    break;

                Marshal.ReleaseComObject(item);
            }

            return "done";
        }

        public void MoveMail(OutlookMessageInfo outlookMessageInfo)
        {
            Outlook.MAPIFolder inbox = this.application.ActiveExplorer().Session
                .GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
            Outlook.Items items = inbox.Items;
            int length = items.Count;
            items.Sort("[ReceivedTime]", true);

            for (int i = 1; i <= length; i++)
            {
                if (items[i] is MailItem item)
                {
                    if (CompareEmail(item, outlookMessageInfo))
                    {
                        if (!string.IsNullOrEmpty(outlookMessageInfo.MoveEmailToFolderNameIfMatched))
                        {
                            Outlook.MAPIFolder destFolder =
                                inbox.Folders[outlookMessageInfo.MoveEmailToFolderNameIfMatched];
                            item.Move(destFolder);
                        }

                        if (outlookMessageInfo.OnlySaveLatest)
                        {
                            break;
                        }
                    }
                    Marshal.ReleaseComObject(item);
                }
            }
        }

        private List<MailItem> SearchFolder(MAPIFolder folder, OutlookMessageInfo filter)
        {
            List<MailItem> matchedItems = new List<MailItem>();

            Items items = folder.Items;
            items.Sort("[ReceivedTime]", true);

            foreach (MailItem item in items)
            {
                if (CompareEmail(item, filter))
                {
                    matchedItems.Add(item);
                }
                else
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            if (folder.Folders.Count > 0)
            {
                foreach (MAPIFolder childFolder in folder.Folders)
                {
                    var matchedSubItems = SearchFolder(childFolder, filter);

                    if (matchedSubItems != null && matchedSubItems.Count != 0)
                    {
                        matchedItems.AddRange(matchedSubItems);
                    }

                    Marshal.ReleaseComObject(childFolder);
                }
            }

            return matchedItems;
        }

        private OutlookMessageInfo FixInvalidFilterString(OutlookMessageInfo outlookMessageInfo)
        {
            char[] trimChars = new char[] { ' ', '\r', '\n' };
            outlookMessageInfo.SenderEmail = outlookMessageInfo.SenderEmail?.Trim(trimChars);
            outlookMessageInfo.SenderName = outlookMessageInfo.SenderName?.Trim(trimChars);
            outlookMessageInfo.Subject = outlookMessageInfo.Subject?.Trim(trimChars);
            outlookMessageInfo.Body = outlookMessageInfo.Body?.Trim(trimChars);
            return outlookMessageInfo;
        }

        private bool CompareEmail(MailItem item, OutlookMessageInfo outlookMessageInfo)
        {
            bool validEmail = true, validEmailName = true, validEmailSubject = true, validEmailBody = true;
            if (!string.IsNullOrWhiteSpace(outlookMessageInfo.SenderEmail))
                validEmail = item.SenderEmailAddress.Equals(outlookMessageInfo.SenderEmail);
            if (!string.IsNullOrWhiteSpace(outlookMessageInfo.SenderName))
                validEmailName = item.SenderName.Equals(outlookMessageInfo.SenderName);
            if (!string.IsNullOrWhiteSpace(outlookMessageInfo.Subject))
                validEmailSubject = item.Subject.Contains(outlookMessageInfo.Subject);
            if (!string.IsNullOrWhiteSpace(outlookMessageInfo.Body))
                validEmailBody = item.Body.Contains(outlookMessageInfo.Body);
            if (validEmail && validEmailName && validEmailSubject && validEmailBody)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool CompareEmail(MailItem item, OutlookNewEmailTriggerInfo outlookTriggerInfo)
        {
            bool validFromEmail = true, validToEmail = true, validEmailName = true, validEmailSubject = true, validEmailBody = true;
            string senderAddr = null;

            //Handle internal mail references
            if (item.SenderEmailAddress.Contains("@"))
            {
                senderAddr = item.SenderEmailAddress;
            }
            else if (item.Sender.AddressEntryUserType == OlAddressEntryUserType.olExchangeUserAddressEntry
                || item.Sender.AddressEntryUserType == OlAddressEntryUserType.olExchangeRemoteUserAddressEntry)
            {
                senderAddr = item.Sender.GetExchangeUser()?.PrimarySmtpAddress;
            }

            if (!string.IsNullOrEmpty(outlookTriggerInfo.From))
                validFromEmail = senderAddr.Equals(outlookTriggerInfo.From);
            if (!string.IsNullOrEmpty(outlookTriggerInfo.Subject))
                validEmailSubject = item.Subject.Contains(outlookTriggerInfo.Subject);
            if (!string.IsNullOrEmpty(outlookTriggerInfo.To))
                validEmailBody = item.To.Contains(outlookTriggerInfo.To);
            if (!string.IsNullOrEmpty(outlookTriggerInfo.Body))
                validEmailBody = item.Body.Contains(outlookTriggerInfo.Body);

            return validFromEmail && validToEmail && validEmailName && validEmailSubject && validEmailBody;
        }

        private string GetReadableSourceAddress(MailItem item)
        {
            string senderAddr = null;

            if (item.SenderEmailAddress.Contains("@"))
            {
                senderAddr = item.SenderEmailAddress;
            }
            else if (item.Sender.AddressEntryUserType == OlAddressEntryUserType.olExchangeUserAddressEntry
                || item.Sender.AddressEntryUserType == OlAddressEntryUserType.olExchangeRemoteUserAddressEntry)
            {
                senderAddr = item.Sender.GetExchangeUser()?.PrimarySmtpAddress;
            }

            return senderAddr;
        }

        private Outlook.MAPIFolder GetRequestedInbox(string outlookAccountUserName)
        {
            Outlook.MAPIFolder inbox = null;
            if (string.IsNullOrEmpty(outlookAccountUserName))
            {
                inbox = this.application.ActiveExplorer().Session.
                    GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
            }
            else
            {
                Outlook.Accounts accounts = application.Session.Accounts;
                foreach (Outlook.Account account in accounts)
                {
                    if (account.UserName.Equals(outlookAccountUserName))
                    {
                        inbox = account.Session.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
                        break;
                    }
                }
            }

            return inbox;
        }

        
    }
}
