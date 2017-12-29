﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace socket.core.Client
{
    /// <summary>
    /// 客户端基础类
    /// </summary>
    public class TcpClients
    {
        /// <summary>
        /// 套接字
        /// </summary>
        private Socket socket;
        /// <summary>
        /// 用于每个套接字I/O操作的缓冲区大小
        /// </summary>
        private int m_receiveBufferSize;
        /// <summary>
        /// 接受缓存
        /// </summary>
        private byte[] buffer_receive;
        /// <summary>
        /// 发送对象
        /// </summary>
        private SocketAsyncEventArgs sendSocketAsyncEventArgs;
        /// <summary>
        /// 接收对象
        /// </summary>
        private SocketAsyncEventArgs receiveSocketAsyncEventArgs;
        /// <summary>
        /// 连接成功事件
        /// </summary>
        public event Action<bool> OnAccept;
        /// <summary>
        /// 接收通知事件
        /// </summary>
        public event Action<byte[]> OnReceive;
        /// <summary>
        /// 断开连接通知事件
        /// </summary>
        public event Action OnClose;

        /// <summary>
        /// 设置基本配置
        /// </summary>
        /// <param name="receiveBufferSize">用于每个套接字I/O操作的缓冲区大小(接收端)</param>
        public TcpClients(int receiveBufferSize)
        {
            m_receiveBufferSize = receiveBufferSize;
            Init();
        }

        /// <summary>
        /// 初始化
        /// </summary>
        private void Init()
        {
            buffer_receive = new byte[m_receiveBufferSize];          
        }
       
        /// <summary>
        /// 连接服务器
        /// </summary>
        /// <param name="ip">ip地址或域名</param>
        /// <param name="port">端口</param>
        public void Connect(string ip, int port)
        {
            IPAddress ipaddr;
            if (!IPAddress.TryParse(ip, out ipaddr))
            {
                IPAddress[] iplist = Dns.GetHostAddresses(ip);
                if (iplist != null && iplist.Length > 0)
                {
                    ipaddr = iplist[0];
                }
            }
            IPEndPoint localEndPoint = new IPEndPoint(ipaddr, port);            
            socket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            SocketAsyncEventArgs connSocketAsyncEventArgs = new SocketAsyncEventArgs();
            connSocketAsyncEventArgs.RemoteEndPoint = localEndPoint;
            connSocketAsyncEventArgs.Completed += ConnSocketAsyncEventArgs_Completed;
            bool willConnect = socket.ConnectAsync(connSocketAsyncEventArgs);
            if (!willConnect)
            {
                ConnSocketAsyncEventArgs_Completed(null, connSocketAsyncEventArgs);
            }
        }
        
        /// <summary>
        /// 连接回调事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ConnSocketAsyncEventArgs_Completed(object sender, SocketAsyncEventArgs e)
        {
            if(e.SocketError==SocketError.Success)
            {
                sendSocketAsyncEventArgs = new SocketAsyncEventArgs();
                sendSocketAsyncEventArgs.Completed += ReadSocketAsyncEventArgs_Completed;
                receiveSocketAsyncEventArgs = new SocketAsyncEventArgs();
                receiveSocketAsyncEventArgs.SetBuffer(buffer_receive, 0, buffer_receive.Length);
                receiveSocketAsyncEventArgs.Completed += ReceiveSocketAsyncEventArgs_Completed;
                socket.ReceiveAsync(receiveSocketAsyncEventArgs);
                if(OnAccept!=null)
                {
                    OnAccept(true);
                }
            }
            else
            {
                if (OnAccept != null)
                {
                    OnAccept(false);
                }
            }
        }

        /// <summary>
        /// 发送数据到服务端
        /// </summary>
        /// <param name="data">数据</param>
        /// <param name="offset">偏移位</param>
        /// <param name="length">长度</param>
        public void Send(byte[] data, int offset, int length)
        {
            sendSocketAsyncEventArgs.SetBuffer(data, offset, length);
            bool willRaiseEvent = socket.SendAsync(sendSocketAsyncEventArgs);
            if (!willRaiseEvent)
            {
                ReadSocketAsyncEventArgs_Completed(null, sendSocketAsyncEventArgs);
            }
        }

        /// <summary>
        /// 发送回调
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ReadSocketAsyncEventArgs_Completed(object sender, SocketAsyncEventArgs e)
        {
        }

        /// <summary>
        /// 接受回调
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ReceiveSocketAsyncEventArgs_Completed(object sender, SocketAsyncEventArgs e)
        {
            if(e.BytesTransferred>0&&e.SocketError==SocketError.Success)
            {
                byte[] data = new byte[e.BytesTransferred];
                Array.Copy(e.Buffer, e.Offset, data, 0, e.BytesTransferred);
                if(OnReceive!=null)
                {
                    OnReceive(data);
                }              
                //将收到的数据回显给客户端             
                bool willRaiseEvent = socket.ReceiveAsync(e);
                if (!willRaiseEvent)
                {
                    ReceiveSocketAsyncEventArgs_Completed(null,e);
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }
        
        /// <summary>
        /// 客户端断开一个连接
        /// </summary>
        /// <param name="e"></param>
        protected void CloseClientSocket(SocketAsyncEventArgs e)
        {
            if (socket.Connected)
            {
                return;
            }
            // 关闭与客户端关联的套接字
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            // 抛出客户端进程已经关闭
            catch (Exception) { }
            socket.Close();
            if(OnClose!=null)
            {
                OnClose();
            }            
        }

        /// <summary>
        /// 客户端主动关闭连接
        /// </summary>
        public void Close()
        {
            CloseClientSocket(receiveSocketAsyncEventArgs);
        }

    }
}
