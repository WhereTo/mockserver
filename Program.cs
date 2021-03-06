﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ServiceModel.Web;
using System.ServiceModel.Description;
using System.Configuration;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Net;
using MockServer;
using Fiddler;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;

namespace WCF_REST
{
    class Program
    {
    

        #region Trap application termination
        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

        private delegate bool EventHandler(CtrlType sig);
        static EventHandler _handler=new EventHandler(Handler);

        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        private static bool Handler(CtrlType sig)
        {
            
            if (sig == CtrlType.CTRL_CLOSE_EVENT)
            {
              
                if (Fiddler.FiddlerApplication.IsStarted())
                {
                    Fiddler.FiddlerApplication.Shutdown();
                }
            }
            return true;
        }
        #endregion
      static string rulesFile = AppDomain.CurrentDomain.BaseDirectory + "Rules.txt";
       
      internal  static RuleParser Rule = new RuleParser(rulesFile); 
      static void Main(string[] args)
      {
         
          SetConsoleCtrlHandler(_handler, true);
          // Start local WCF service on 80 port
          try 
          {
              WebServiceHost svc = new WebServiceHost(typeof(Service1), new Uri[] { new Uri("http://localhost")});
              svc.Open();
          }
          catch(Exception ex)
          {
              Console.WriteLine(ex);
          }
          // Fiddler Core
          int iProcCount = Environment.ProcessorCount;
          int iMinWorkerThreads = Math.Max(16, 6 * iProcCount);
          int iMinIOThreads = iProcCount;
          ThreadPool.SetMinThreads(iMinWorkerThreads, iMinIOThreads);



          // <-- Personalize for your Application, 64 chars or fewer
          Fiddler.FiddlerApplication.SetAppDisplayName("TestControllerFCore");
          FiddlerCoreStartupFlags oFCSF = FiddlerCoreStartupFlags.Default;
          int iPort = int.Parse(ConfigurationManager.AppSettings["localPort"]);
          Fiddler.FiddlerApplication.BeforeRequest += delegate(Fiddler.Session oS)
       {
           oS.bBufferResponse = true;
            
       };
          string encoding = null;
          string result = null;
          Fiddler.FiddlerApplication.BeforeResponse += delegate(Fiddler.Session oS)
          {
              var reqBody = oS.GetRequestBodyAsString();
              result = oS.GetResponseBodyAsString();
              //get encode type
              encoding = oS.oRequest.headers["Accept-Charset"];
              if (encoding == null || encoding == "") 
              {
                  
                  string type = oS.oRequest.headers["Accept"];
                  if(type.Contains("text/html"))
                  {
                      Match meta = Regex.Match(result, "<meta([^<]*)charset=([^<]*)[\"']", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                      encoding = (meta.Groups.Count > 2) ? meta.Groups[2].Value : string.Empty;
                      if (encoding.StartsWith("\""))
                      {
                          encoding = encoding.Substring(1);
                      }
                  }
                  
              }  
              bool needCallConvert =false;
              if (Config.ENABLE_CONVERT_SERVICE)
              {
                  needCallConvert = Rule.NeedCallConvert(new Uri(oS.fullUrl), oS.RequestMethod, reqBody);
                  if (Config.ENABLE_CONVERT_SERVICE&&needCallConvert && (Config.CONVERT_MODE == ConvertMode.Both || Config.CONVERT_MODE == ConvertMode.ReqOnly))
                  {
                      // Convert reqBody before getting mapped response
                      try
                      {
                          WebClient wc = new WebClient();
                          reqBody = System.Text.Encoding.UTF8.GetString(wc.UploadData(Config.CONVERT_REQ_SERVICE_URL, System.Text.Encoding.UTF8.GetBytes(reqBody)));
                      }
                      catch (Exception ex)
                      {
                          Console.WriteLine(ex.ToString());
                      }
                  }
              }
           
              var expectResp = Rule.GetExpectHttpResponse(new Uri(oS.fullUrl), oS.RequestMethod, reqBody);

              if (expectResp != null)
              {
                  var finalRespBody = expectResp.Body;
                  // TODO:Convert resp body before returing resp
                  if (finalRespBody != null && Config.ENABLE_CONVERT_SERVICE && (needCallConvert && (Config.CONVERT_MODE == ConvertMode.Both || Config.CONVERT_MODE == ConvertMode.RespOnly)))
                  {
                      // convert finalRespBody
                      WebClient wc = new WebClient();
                      finalRespBody = wc.UploadData(Config.CONVERT_RESP_SERVICE_URL, finalRespBody);
                  }
                 
                  
                  var newHeader = new HTTPResponseHeaders();
                  newHeader.SetStatus(expectResp.StatusCode, "Fiddler-Generated");
                  if (expectResp.Header != null)
                  {
                      foreach (var key in expectResp.Header.Keys)
                      {
                          newHeader[key.ToString()] = expectResp.Header[key.ToString()];
                          // newHeader.Add(key.ToString(), expectResp.Header[key.ToString()]);
                      }
                      if (finalRespBody != null)
                      {
                          oS.utilAssignResponse(newHeader, finalRespBody);
                      }

                      oS.oResponse.headers["Content-Length"] = expectResp.Body.Length.ToString();
                  }
                  if (expectResp.AdditionalDelay.TotalMilliseconds > 0)
                  {
                     Thread.Sleep(expectResp.AdditionalDelay);
                  }
                  if (expectResp.ReplaceCharacaters != null)
                  {                      
                      if (expectResp.ReplaceCharacaters[2].Equals("str"))
                      {
                          result = result.Replace(expectResp.ReplaceCharacaters[0], expectResp.ReplaceCharacaters[1]);
                      }
                      else
                      {
                          Regex regex = new Regex(expectResp.ReplaceCharacaters[0]);
                          result = regex.Replace(result, expectResp.ReplaceCharacaters[1]);
                      }
                      if (result != null && result!="")
                      {
                          if (encoding == null || encoding == "")
                          {
                              encoding = "utf-8";
                          }
                          var resultArray = System.Text.Encoding.GetEncoding(encoding).GetBytes(result);
                          oS.utilAssignResponse(newHeader, resultArray);
                      }
                  }
                  
              }
              
          };

          Fiddler.FiddlerApplication.Startup(iPort, oFCSF);
          Console.WriteLine("Started. Press <Enter> to exit...");
          Console.ReadLine();
          try
          {
              Fiddler.FiddlerApplication.Shutdown();
          }
          catch (Exception) { }
         
      }

       

      
      
    }
}
