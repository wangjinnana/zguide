﻿//
//  Paranoid Pirate Queue
//
//  Author:     Pepper Garretson
//  Email:      jpgarretson@gmail.com
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ZMQ;

namespace ZMQGuide
{
    class Program
    {
        private const int HEARTBEAT_LIVENSS = 3; //3-5 is reasonable
        private const int HEARTBEAT_INTERVAL = 1000; //msecs

        private const string PPP_READY = "READY";
        private const string PPP_HEARTBEAT = "HEARTBEAT";

        public class Worker
        {
            public byte[] address;
            public DateTime expiry;

            public Worker(byte[] address)
            {
                this.address = address;
                this.expiry = DateTime.Now.AddMilliseconds(HEARTBEAT_INTERVAL * HEARTBEAT_LIVENSS);
            }

            public void ResetExpiry()
            {
                this.expiry = DateTime.Now.AddMilliseconds(HEARTBEAT_INTERVAL * HEARTBEAT_LIVENSS); ;
            }

            public override bool Equals(object obj)
            {
                if (obj.GetType() != typeof(Worker))
                {
                    return false;
                }
                else
                {
                    return this.address.SequenceEqual((obj as Worker).address);
                }
            }

            public override int GetHashCode()
            {
                return this.address.GetHashCode();
            }
        }

        static void Main(string[] args)
        {
            using (var context = new Context(1))
            {
                using (Socket frontend = context.Socket(SocketType.ROUTER), backend = context.Socket(SocketType.ROUTER))
                {
                    frontend.Bind("tcp://*:5555"); // For Clients
                    backend.Bind("tcp://*:5556"); // For Workers

                    //  Queue of available workers
                    var workerQueue = new List<Worker>();

                    backend.PollInHandler += (socket, revents) =>
                    {
                        var zmsg = new ZMessage(socket);

                        byte[] identity = zmsg.Unwrap();

                        //Any sign of life from worker means it's ready, Only add it to the queue if it's not in there already
                        Worker worker = null;

                        if (workerQueue.Count > 0)
                        {
                            var workers = workerQueue.Where(x => x.address.SequenceEqual(identity));

                            if (workers.Count() > 0)
                                worker = workers.Single();
                        }

                        if (worker == null)
                        {
                            workerQueue.Add(new Worker(identity));
                        }

                        //Return reply to client if it's not a control message
                        switch (Encoding.Unicode.GetString(zmsg.Address))
                        {
                            case PPP_READY:
                                Console.WriteLine("Worker " + Encoding.Unicode.GetString(identity) + " is ready...");
                                break;
                            case PPP_HEARTBEAT:
                                bool found = false;

                                //Worker Refresh
                                if (worker != null)
                                {
                                    found = true;
                                    worker.ResetExpiry();
                                }

                                if (!found)
                                {
                                    Console.WriteLine("E: worker " + Encoding.Unicode.GetString(identity) + " not ready...");
                                }
                                break;
                            default:
                                zmsg.Send(frontend);
                                break;
                        };
                    };

                    frontend.PollInHandler += (socket, revents) =>
                    {
                        //  Now get next client request, route to next worker
                        //  Dequeue and drop the next worker address
                        var zmsg = new ZMessage(socket);

                        Worker w = workerQueue[0];
                        zmsg.Wrap(w.address, new byte[0]);
                        workerQueue.RemoveAt(0);
                        
                        zmsg.Send(backend);
                    };

                    DateTime heartbeat_at = DateTime.Now.AddMilliseconds(HEARTBEAT_INTERVAL);

                    while (true)
                    {
                        //Only poll frontend only if there are workers ready
                        if (workerQueue.Count > 0)
                        {
                            List<Socket> pollItems = new List<Socket>(new Socket[] { frontend, backend });
                            Context.Poller(pollItems, HEARTBEAT_INTERVAL * 1000);
                        }
                        else
                        {
                            List<ZMQ.Socket> pollItems = new List<Socket>(new Socket[] { backend });
                            Context.Poller(pollItems, HEARTBEAT_INTERVAL * 1000);
                        }

                        //Send heartbeats to idle workers if it's time
                        if (DateTime.Now >= heartbeat_at)
                        {
                            foreach (var worker in workerQueue)
                            {
                                ZMessage zmsg = new ZMessage(PPP_HEARTBEAT);
                                zmsg.Wrap(worker.address, new byte[0]);
                                zmsg.Send(backend);
                            }

                            heartbeat_at = DateTime.Now.AddMilliseconds(HEARTBEAT_INTERVAL);
                        }
                    }
                }
            }
        }
    }
}