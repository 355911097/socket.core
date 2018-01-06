﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using System.Collections.Concurrent;
using socket.core.Common;

namespace socket.core.Server
{
    /// <summary>
    /// tcp Socket监听基库
    /// </summary>
    internal class TcpServer
    {
        /// <summary>
        /// 同时处理的最大连接数
        /// </summary>
        private int m_numConnections;
        /// <summary>
        /// 用于每个套接字I/O操作的缓冲区大小
        /// </summary>
        private int m_receiveBufferSize;
        /// <summary>
        /// 所有套接字接收操作的一个可重用的大型缓冲区集合。
        /// </summary>
        private BufferManager m_bufferManager;
        /// <summary>
        /// 用于监听传入连接请求的套接字
        /// </summary>
        private Socket listenSocket;
        /// <summary>
        /// 接受端SocketAsyncEventArgs对象重用池，接受套接字操作
        /// </summary>
        private SocketAsyncEventArgsPool m_receivePool;
        /// <summary>
        /// 发送端SocketAsyncEventArgs对象重用池，发送套接字操作
        /// </summary>
        private SocketAsyncEventArgsPool m_sendPool;
        /// <summary>
        /// 超时，如果超时，服务端断开连接，客户端需要重连操作
        /// </summary>
        private int overtime;
        /// <summary>
        /// 超时检查间隔时间(秒)
        /// </summary>
        private int overtimecheck = 10;
        /// <summary>
        /// 能接到最多客户端个数的原子操作
        /// </summary>
        private Semaphore m_maxNumberAcceptedClients;
        /// <summary>
        /// 已经连接的对象池
        /// </summary>
        internal ConcurrentBag<ConnectClient> connectClient;
        /// <summary>
        /// 发送对象最小值
        /// </summary>
        private int m_minSendSocketAsyncEventArgs = 10000;
        /// <summary>
        /// 连接成功事件
        /// </summary>
        internal event Action<Guid> OnAccept;
        /// <summary>
        /// 接收通知事件
        /// </summary>
        internal event Action<Guid, byte[]> OnReceive;
        /// <summary>
        /// 断开连接通知事件
        /// </summary>
        internal event Action<Guid> OnClose;

        /// <summary>
        /// 设置基本配置
        /// </summary>   
        /// <param name="numConnections">同时处理的最大连接数</param>
        /// <param name="receiveBufferSize">用于每个套接字I/O操作的缓冲区大小(接收端)</param>
        /// <param name="overtime">超时时长,单位秒.(每10秒检查一次)，当值为0时，不设置超时</param>
        internal TcpServer(int numConnections, int receiveBufferSize, int overTime)
        {
            overtime = overTime;
            m_numConnections = numConnections;
            m_receiveBufferSize = receiveBufferSize;
            m_bufferManager = new BufferManager(receiveBufferSize * m_numConnections, receiveBufferSize);
            m_receivePool = new SocketAsyncEventArgsPool(m_numConnections);
            //当连接数量不大时，增加发送操作对象
            if((Int32)(m_numConnections * 1.5) < m_minSendSocketAsyncEventArgs)
            {
                m_sendPool = new SocketAsyncEventArgsPool(m_minSendSocketAsyncEventArgs);
            }
            else
            {
                m_sendPool = new SocketAsyncEventArgsPool((Int32)(m_numConnections * 1.5));
            }            
            m_maxNumberAcceptedClients = new Semaphore(m_numConnections, m_numConnections);
            Init();
        }

        /// <summary>
        /// 初始化服务器通过预先分配的可重复使用的缓冲区和上下文对象。这些对象不需要预先分配或重用，但这样做是为了说明API如何可以易于用于创建可重用对象以提高服务器性能。
        /// </summary>
        private void Init()
        {
            connectClient = new ConcurrentBag<ConnectClient>();
            //分配一个大字节缓冲区，所有I/O操作都使用一个。这个侍卫对内存碎片
            m_bufferManager.InitBuffer();
            //预分配的接受对象池socketasynceventargs，并分配缓存
            SocketAsyncEventArgs saea_receive;
            //分配的发送对象池socketasynceventargs，不分配缓存
            SocketAsyncEventArgs saea_send;
            for (int i = 0; i < m_numConnections; i++)
            {
                //预先接受端分配一组可重用的消息
                saea_receive = new SocketAsyncEventArgs();
                saea_receive.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                saea_receive.UserToken = new AsyncUserToken();
                //分配缓冲池中的字节缓冲区的socketasynceventarg对象
                m_bufferManager.SetBuffer(saea_receive);
                m_receivePool.Push(saea_receive);
            }
            //预先发送端数量是接收端的1.5倍。以防止异步阻塞时，发送端不够用, 如果小于m_minSendSocketAsyncEventArgs，则默认为m_minSendSocketAsyncEventArgs个
            if ((Int32)(m_numConnections * 1.5) < m_minSendSocketAsyncEventArgs)
            {
                //预先发送端分配一组可重用的消息
                for (int i = 0; i < m_minSendSocketAsyncEventArgs; i++)
                {
                    saea_send = new SocketAsyncEventArgs();
                    saea_send.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                    saea_send.UserToken = new AsyncUserToken();
                    m_sendPool.Push(saea_send);
                }
            }
            else
            {
                //预先发送端分配一组可重用的消息
                for (int i = 0; i < (Int32)(m_numConnections * 1.5); i++)
                {
                    saea_send = new SocketAsyncEventArgs();
                    saea_send.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                    saea_send.UserToken = new AsyncUserToken();
                    m_sendPool.Push(saea_send);
                }
            }
        }

        /// <summary>
        /// 启动tcp服务侦听
        /// </summary>       
        /// <param name="port">监听端口</param>
        internal void Start(int port)
        {
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, port);
            //创建listens是传入的连接插座。
            listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            //绑定端口
            listenSocket.Bind(localEndPoint);
            //挂起的连接队列的最大长度。
            listenSocket.Listen(10000);
            //在监听套接字上接受
            StartAccept(null);
            //超时机制
            if (overtime > 0)
            {
                Thread heartbeat = new Thread(new ThreadStart(() =>
                {
                    Heartbeat();
                }));
                heartbeat.Priority = ThreadPriority.Lowest;
                heartbeat.Start();
            }
        }

        /// <summary>
        /// 超时机制
        /// </summary>
        private void Heartbeat()
        {
            //计算超时次数 ，超过count就当客户端断开连接。服务端清除该连接资源
            int count = overtime / overtimecheck;
            while (true)
            {
                ConnectClient client = connectClient.FirstOrDefault(P => P.keep_alive >= count);
                if (client != null)
                {
                    client.keep_alive = 0;
                    CloseClientSocket(client.saea_receive);
                }
                foreach (var item in connectClient)
                {
                    item.keep_alive++;
                }
                Thread.Sleep(overtimecheck * 1000);
            }
        }

        #region Accept

        /// <summary>
        /// 开始接受客户端的连接请求的操作。
        /// </summary>
        /// <param name="acceptEventArg">发布时要使用的上下文对象服务器侦听套接字上的接受操作</param>
        private void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(AcceptEventArg_Completed);
            }
            else
            {
                // 套接字必须被清除，因为上下文对象正在被重用。
                acceptEventArg.AcceptSocket = null;
            }
            m_maxNumberAcceptedClients.WaitOne();
            if (!listenSocket.AcceptAsync(acceptEventArg))
            {
                ProcessAccept(acceptEventArg);
            }
        }

        /// <summary>
        /// 当异步连接完成时调用此方法
        /// </summary>
        /// <param name="e">操作对象</param>
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            //从接受端重用池获取一个新的SocketAsyncEventArgs对象
            SocketAsyncEventArgs receiveEventArgs = m_receivePool.Pop();
            ((AsyncUserToken)receiveEventArgs.UserToken).Socket = e.AcceptSocket;
            //一旦客户机连接，就向连接发送一个接收。
            if (!e.AcceptSocket.ReceiveAsync(receiveEventArgs))
            {
                ProcessReceive(receiveEventArgs);
            }
            //把连接到的客户端信息添加到集合中
            ConnectClient connecttoken = new ConnectClient();
            connecttoken.connectId = Guid.NewGuid();
            connecttoken.socket = e.AcceptSocket;
            connecttoken.saea_receive = receiveEventArgs;
            connectClient.Add(connecttoken);
            //回调
            if (OnAccept != null)
            {
                OnAccept.BeginInvoke(connecttoken.connectId, null, null);
            }
            // 接受第二连接的要求
            StartAccept(e);
        }

        /// <summary>
        /// 这种方法与socket.acceptasync回调方法操作，并在接受操作完成时调用。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">操作对象</param>
        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        /// <summary>
        /// 客户端断开一个连接
        /// </summary>
        /// <param name="e">操作对象</param>
        private void CloseClientSocket(SocketAsyncEventArgs e)
        {
            AsyncUserToken token = e.UserToken as AsyncUserToken;
            if (!token.Socket.Connected)
            {
                return;
            }
            // 关闭与客户端关联的套接字
            try
            {
                token.Socket.Shutdown(SocketShutdown.Both);
            }
            // 抛出客户端进程已经关闭
            catch (Exception) { }
            token.Socket.Close();
            m_maxNumberAcceptedClients.Release();
            //释放SocketAsyncEventArgs，以便其他客户端可以重用它们
            if (e.LastOperation == SocketAsyncOperation.Receive)
            {
                m_receivePool.Push(e);
                ConnectClient conn = connectClient.FirstOrDefault(P => P.saea_receive == e);
                if (conn != null)
                {                    
                    if (OnClose != null)
                    {
                        OnClose.BeginInvoke(conn.connectId,null,null);
                    }
                    connectClient.TryTake(out conn);
                }
            }
        }

        /// <summary>
        /// 客户端断开一个连接
        /// </summary>
        /// <param name="connectId">连接标记</param>
        internal void Close(Guid connectId)
        {
            ConnectClient conn = connectClient.FirstOrDefault(P => P.connectId == connectId);
            if (conn == null)
            {
                return;
            }
            CloseClientSocket(conn.saea_receive);
        }

        #endregion

        /// <summary>
        /// 每当套接字上完成接收或发送操作时，都会调用此方法。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">与完成的接收操作关联的SocketAsyncEventArg</param>
        private void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            //确定刚刚完成哪种类型的操作并调用相关的处理程序
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("套接字上完成的最后一个操作不是接收或发送。");
            }
        }

        #region 接受处理 receive

        /// <summary>
        /// 接受处理回调
        /// </summary>
        /// <param name="e">操作对象</param>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            //检查远程主机是否关闭连接
            AsyncUserToken token = (AsyncUserToken)e.UserToken;
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                byte[] data = new byte[e.BytesTransferred];
                Array.Copy(e.Buffer, e.Offset, data, 0, e.BytesTransferred);
                //回调               
                if (OnReceive != null)
                {
                    ConnectClient connect = connectClient.FirstOrDefault(P => P.saea_receive == e);
                    if (connect != null)
                    {       
                        OnReceive.BeginInvoke(connect.connectId, data, null, null);
                    }
                }
                //如果接收到数据，超时记录设置为0
                if (overtime > 0)
                {
                    ConnectClient client = connectClient.FirstOrDefault(P => P.saea_receive == e);
                    if (client != null)
                    {
                        client.keep_alive = 0;
                    }
                }
                //将收到的数据回显给客户端             
                if (!token.Socket.ReceiveAsync(e))
                {
                    ProcessReceive(e);
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        #endregion


        #region 发送处理 send

        /// <summary>
        /// 异步发送消息 
        /// </summary>
        /// <param name="connectId">连接ID</param>
        /// <param name="data">数据</param>
        /// <param name="offset">偏移位</param>
        /// <param name="length">长度</param>
        internal void Send(Guid connectId, byte[] data, int offset, int length)
        {
            ConnectClient connect = connectClient.FirstOrDefault(P => P.connectId == connectId);
            if(connect==null)
            {
                return;
            }
            SocketAsyncEventArgs sendEventArgs = m_sendPool.Pop();
            ((AsyncUserToken)sendEventArgs.UserToken).Socket = connect.socket;
            sendEventArgs.SetBuffer(data, offset, length);
            if (!connect.socket.SendAsync(sendEventArgs))
            {
                ProcessSend(sendEventArgs);
            }
        }

        /// <summary>
        /// 发送回调
        /// </summary>
        /// <param name="e">操作对象</param>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                m_sendPool.Push(e);
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        #endregion

        /// <summary>
        /// 给连接对象设置附加数据
        /// </summary>
        /// <param name="connectId">连接标识</param>
        /// <param name="data">附加数据</param>
        /// <returns>true:设置成功,false:设置失败</returns>
        internal bool SetAttached(Guid connectId, dynamic data)
        {
            ConnectClient connect = connectClient.FirstOrDefault(P => P.connectId == connectId);
            if(connect==null)
            {
                return false;
            }
            connect.attached = data;
            return true;
        }

        /// <summary>
        /// 获取连接对象的附加数据
        /// </summary>
        /// <param name="connectId">连接标识</param>
        /// <returns>附加数据，如果没有找到则返回null</returns>
        internal dynamic GetAttached(Guid connectId)
        {
            ConnectClient connect = connectClient.FirstOrDefault(P => P.connectId == connectId);
            if (connect == null)
            {
                return null;
            }
            else
            {
                return connect.attached;
            }
        }

    }

}