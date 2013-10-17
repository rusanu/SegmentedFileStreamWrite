using System;
using System.Net;
using System.Threading;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Multistream
{
    class Program
    {
        static volatile int running = 0;
        static ManualResetEvent evDone = new ManualResetEvent(false);

        private static void CompleteOne()
        {
            int left = Interlocked.Decrement(ref running);
            if (0 == left)
            {
                evDone.Set();
            }
        }

        static void GetRequest(string url, long fileSize, MemoryMappedFile mmap, int fragNo, int count)
        {
            Interlocked.Increment(ref running);
            long length = fileSize / count;
            long offset = length * fragNo;

            Console.Out.WriteLine("Fragment {0} offset {1} size {2}", fragNo, offset, length);

            Stream file = mmap.CreateViewStream(offset, length);

            HttpWebRequest get = (HttpWebRequest)HttpWebRequest.Create(url);
            get.AddRange(offset, offset + length-1);

            get.BeginGetResponse((ar) =>
                {
                    try
                    {
                        WebResponse resp = get.EndGetResponse(ar);
                        Stream web = resp.GetResponseStream();

                        byte[] buffer = new byte[4096];
                        int position = 0;

                        AsyncCallback cbBuffer = null;
                        cbBuffer = (bar) =>
                             {
                                 try
                                 {
                                     int bytesRead = web.EndRead(bar);
                                     Console.Out.WriteLine("Fragment {0} read {1}", fragNo, bytesRead);
                                     if (0 < bytesRead)
                                     {
                                         // There are better ways to do this, eg. overlap reads and writes using 2 buffers
                                         file.BeginWrite(buffer, 0, bytesRead, (war) =>
                                         {
                                             try
                                             {
                                                 file.EndWrite(war);
                                                 position += bytesRead;
                                                 Console.Out.WriteLine("Fragment {0} write {1}", fragNo, bytesRead);
                                                 web.BeginRead(buffer, 0, buffer.Length, cbBuffer, null);
                                             }
                                             catch (Exception e)
                                             {
                                                 Console.Error.WriteLine(e);
                                                 CompleteOne();
                                             }
                                         }, null);
                                     }
                                     else
                                     {
                                         web.Dispose();
                                         file.Dispose();
                                         Console.Out.WriteLine("Fragment {0} done", fragNo);
                                         CompleteOne();
                                     }
                                 }
                                 catch (Exception e)
                                 {
                                     Console.Error.WriteLine(e);
                                     CompleteOne();
                                 }
                             };
                        // start reading
                        web.BeginRead(buffer, 0, buffer.Length, cbBuffer, null);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine(e);
                        CompleteOne();
                    }
                }, null);
        }

        static void Main(string[] args)
        {
            try
            {
                string url = "...";

                HttpWebRequest getHeader = (HttpWebRequest)HttpWebRequest.Create(url);
                getHeader.Method = "HEAD";

                var respHdr = getHeader.GetResponse();
                long fileSize = respHdr.ContentLength;

                Console.WriteLine("File is {0} size", fileSize);

                // create the result file
                //
                using (var mmap = MemoryMappedFile.CreateFromFile(@"c:\temp\...", FileMode.Create, "foo", fileSize, MemoryMappedFileAccess.ReadWrite))
                {
                    // +1 for ourself. Prevents premature signal
                    running = 1;

                    // Create split requests

                    GetRequest(url, fileSize, mmap, 0, 4);
                    GetRequest(url, fileSize, mmap, 1, 4);
                    GetRequest(url, fileSize, mmap, 2, 4);
                    GetRequest(url, fileSize, mmap, 3, 4);

                    // Decrement our own +1
                    CompleteOne();

                    evDone.WaitOne();
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
            }
        }
    }
}
