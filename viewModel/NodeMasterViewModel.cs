using System;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Threading;

using SuperSocket.ServerManager.Model;
using SuperSocket.SocketBase.Metadata;
using SuperSocket.ServerManager.Client.Command;
using SuperSocket.ServerManager.Client.Config;

using WebSocket4Net;
using DynamicViewModel;
using Newtonsoft.Json.Linq;


namespace SuperSocket.ServerManager.Client.ViewModel
{

    public partial class NodeMasterViewModel : ViewModelBase
    {

        private bool m_LoginFailed = false;

        private AgentWebSocket m_WebSocket;
        private NodeConfig m_Config;
        private Timer m_ReconnectTimer;

        private List<KeyValuePair<string, StatusInfoAttribute[]>> m_ServerStatusMetadataSource;      

        private StatusInfoAttribute[] m_ColumnAttributes;
        private StatusInfoAttribute[] m_NodeDetailAttributes;        

        public NodeConfig Config
        {

            get
            {

                return m_Config;

            }

        }

        public NodeMasterViewModel(NodeConfig config)
        {

            m_Config = config;

            Name = m_Config.Name;

            ConnectCommand = new DelegateCommand(ExecuteConnectCommand);

            ThreadPool.QueueUserWorkItem((c) => InitializeWebSocket((NodeConfig)c), config);

        }

        void InitializeWebSocket(NodeConfig config)
        {

            try
            {

                m_WebSocket = new AgentWebSocket(config.Uri);

            }

            catch (Exception)
            {

                ErrorMessage = "Invalid server URI!";
                State = NodeState.Offline;

                return;

            }

            m_WebSocket.AllowUnstrustedCertificate = true;
            m_WebSocket.Closed += new EventHandler(WebSocket_Closed);
            m_WebSocket.Error += new EventHandler<ClientEngine.ErrorEventArgs>(WebSocket_Error);
            m_WebSocket.Opened += new EventHandler(WebSocket_Opened);
            m_WebSocket.On<string>(CommandName.UPDATE, OnServerUpdated);

            StartConnect();

        }

        void StartConnect()
        {

            m_LoginFailed = false;

            if (m_WebSocket == null)
            {

                return;

            }                

            State = NodeState.Connecting;

            m_WebSocket.Open();

        }

        void WebSocket_Opened(object sender, EventArgs e)
        {

            AgentWebSocket websocket = sender as AgentWebSocket;

            State = NodeState.Logging;

            dynamic loginInfo = new ExpandoObject();

            loginInfo.UserName = m_Config.UserName;
            loginInfo.Password = m_Config.Password;

            websocket.Query<dynamic>(CommandName.LOGIN, (object)loginInfo, OnLoggedIn);

        }

        private StatusInfoAttribute[] GetCommonColumns(IList<StatusInfoAttribute[]> source)
        {

            List<StatusInfoAttribute> all = new List<StatusInfoAttribute>();

            foreach (var list in source)
            {

                all.AddRange(list);

            }

            return all.GroupBy(c => c.Key).Where(g => g.Count() == source.Count).Select(g => g.FirstOrDefault()).ToArray();

        }

        void OnLoggedIn(dynamic result)
        {

            if (result["Result"].ToObject<bool>())
            {

                m_ServerStatusMetadataSource = result["ServerMetadataSource"].ToObject<List<KeyValuePair<string, StatusInfoAttribute[]>>>();
                
                BuildGridColumns(m_ServerStatusMetadataSource.FirstOrDefault(p => string.IsNullOrEmpty(p.Key)).Value, GetCommonColumns(m_ServerStatusMetadataSource.Where(p => !string.IsNullOrEmpty(p.Key)).Select(c => c.Value).ToArray()));
                
                var nodeInfo = DynamicViewModelFactory.Create(result["NodeStatus"].ToString());

                GlobalInfo = nodeInfo.BootstrapStatus;

                var instances = nodeInfo.InstancesStatus as IEnumerable<DynamicViewModel.DynamicViewModel>;

                Instances = new ObservableCollection<DynamicViewModel.DynamicViewModel>(instances.Select(i =>
                    {
                        var startCommand = new DelegateCommand<DynamicViewModel.DynamicViewModel>(ExecuteStartCommand, CanExecuteStartCommand);
                        var stopCommand = new DelegateCommand<DynamicViewModel.DynamicViewModel>(ExecuteStopCommand, CanExecuteStopCommand);

                        i.PropertyChanged += (s, e) =>
                            {
                                if (string.IsNullOrEmpty(e.PropertyName)
                                    || e.PropertyName.Equals("IsRunning", StringComparison.OrdinalIgnoreCase))
                                {
                                    startCommand.RaiseCanExecuteChanged();
                                    stopCommand.RaiseCanExecuteChanged();
                                }
                            };

                        i.Set("StartCommand", startCommand);
                        i.Set("StopCommand", stopCommand);

                        return i;
                    }));

                State = NodeState.Connected;
                LastUpdatedTime = DateTime.Now;

            }
            else
            {

                m_LoginFailed = true;

                m_WebSocket.Close();

                ErrorMessage = "Logged in failed!";

            }

        }

        private bool CanExecuteStartCommand(DynamicViewModel.DynamicViewModel target)
        {

            string isRunning = ((JValue)((DynamicViewModel.DynamicViewModel)target["Values"])["IsRunning"]).ToString();

            return "False".Equals(isRunning, StringComparison.OrdinalIgnoreCase);

        }

        private void ExecuteStartCommand(DynamicViewModel.DynamicViewModel target)
        {

            m_WebSocket.Query<dynamic>(CommandName.START, ((JValue)target["Name"]).Value, OnActionCallback);

        }

        private bool CanExecuteStopCommand(DynamicViewModel.DynamicViewModel target)
        {

            string isRunning = ((JValue)((DynamicViewModel.DynamicViewModel)target["Values"])["IsRunning"]).ToString();

            return "True".Equals(isRunning, StringComparison.OrdinalIgnoreCase);

        }

        private void ExecuteStopCommand(DynamicViewModel.DynamicViewModel target)
        {

            m_WebSocket.Query<dynamic>(CommandName.STOP, ((JValue)target["Name"]).Value, OnActionCallback);

        }

        void OnServerUpdated(string result)
        {

            dynamic nodeInfo = DynamicViewModelFactory.Create(result);

            Dispatcher.BeginInvoke((Action<dynamic>)OnServerUpdated, nodeInfo);

        }

        void OnServerUpdated(dynamic nodeInfo)
        {

            this.GlobalInfo.UpdateProperties(nodeInfo.BootstrapStatus);

            var instances = nodeInfo.InstancesStatus as IEnumerable<DynamicViewModel.DynamicViewModel>;

            foreach (var i in instances)
            {

                var targetInstance = m_Instances.FirstOrDefault(x => ((JValue)x["Name"]).Value.ToString().Equals(((JValue)i["Name"]).Value.ToString(), StringComparison.OrdinalIgnoreCase));

                if (targetInstance != null)
                {

                    targetInstance.UpdateProperties(i);

                    ((DelegateCommand<DynamicViewModel.DynamicViewModel>)targetInstance["StartCommand"]).RaiseCanExecuteChanged();
                    ((DelegateCommand<DynamicViewModel.DynamicViewModel>)targetInstance["StopCommand"]).RaiseCanExecuteChanged();

                }

            }

            LastUpdatedTime = DateTime.Now;

        }

        void OnActionCallback(string token, dynamic result)
        {

            if (result["Result"].ToObject<bool>())
            {

                var nodeInfo = ((JObject)result["NodeStatus"]).ToDynamic(new DynamicViewModel.DynamicViewModel());

                Dispatcher.BeginInvoke((Action<dynamic>)OnServerUpdated, nodeInfo);

            }
            else
            {

                ErrorMessage = result["Message"].ToString();

            }

        }

        void BuildGridColumns(StatusInfoAttribute[] nodeAttributes, StatusInfoAttribute[] fieldAttributes)
        {

            m_NodeDetailAttributes = nodeAttributes;
            m_ColumnAttributes = fieldAttributes.OrderBy(a => a.Order).ToArray();

        }

        void WebSocket_Error(object sender, ClientEngine.ErrorEventArgs e)
        {

            if (e.Exception != null)
            {

                if (e.Exception is SocketException && ((SocketException)e.Exception).ErrorCode == (int)SocketError.AccessDenied)
                {

                    ErrorMessage = (new SocketException((int)SocketError.ConnectionRefused)).Message;

                }                
                else
                {

                    ErrorMessage = e.Exception.StackTrace;

                }                    

                if (m_WebSocket.State == WebSocketState.None && State == NodeState.Connecting)
                {

                    State = NodeState.Offline;

                    OnDisconnected();

                }

            }

        }

        void WebSocket_Closed(object sender, EventArgs e)
        {

            State = NodeState.Offline;

            if (string.IsNullOrEmpty(ErrorMessage))
            {

                ErrorMessage = "The server is offline";

            }               

            OnDisconnected();

        }

        void OnDisconnected()
        {
            
            if (m_LoginFailed)
            {

                return;

            }                

            if (m_ReconnectTimer == null)
            {

                m_ReconnectTimer = new Timer(ReconnectTimerCallback);

            }

            m_ReconnectTimer.Change(1000 * 60 * 5, Timeout.Infinite);//5 minutes

        }

        void ReconnectTimerCallback(object state)
        {

            if (m_WebSocket.State == WebSocketState.Connecting || m_WebSocket.State == WebSocketState.Open)
            {

                return;

            }                

            StartConnect();

        }

        public string Name { get; private set; }

        private DateTime m_LastUpdatedTime;

        public DateTime LastUpdatedTime
        {

            get
            {

                return m_LastUpdatedTime;

            }

            set
            {

                m_LastUpdatedTime = value;

                RaisePropertyChanged("LastUpdatedTime");

            }

        }

        private string m_ErrorMessage;

        public string ErrorMessage
        {

            get
            {

                return m_ErrorMessage;

            }

            set
            {

                m_ErrorMessage = value;

                RaisePropertyChanged("ErrorMessage");

            }

        }

        private NodeState m_State = NodeState.Offline;

        public NodeState State
        {

            get
            {

                return m_State;

            }

            set
            {

                m_State = value;

                RaisePropertyChanged("State");

            }

        }

        public DelegateCommand ConnectCommand { get; private set; }

        private void ExecuteConnectCommand()
        {

            StartConnect();

        }

        private ObservableCollection<DynamicViewModel.DynamicViewModel> m_Instances;

        public ObservableCollection<DynamicViewModel.DynamicViewModel> Instances
        {

            get
            {

                return m_Instances;

            }

            set
            {

                m_Instances = value;

                RaisePropertyChanged("Instances");

            }

        }

        private DynamicViewModel.DynamicViewModel m_GlobalInfo;

        public DynamicViewModel.DynamicViewModel GlobalInfo
        {

            get
            {

                return m_GlobalInfo;

            }

            set
            {

                m_GlobalInfo = value;

                RaisePropertyChanged("GlobalInfo");

            }

        }

        public void DataGridLoaded(object sender, RoutedEventArgs e)
        {

            DataGrid grid = sender as DataGrid;

            if (m_ColumnAttributes == null)
            {

                return;

            }
                
            string[] existingColumns = grid.Columns.Select(c => c.Header.ToString()).ToArray();

            foreach (var a in m_ColumnAttributes)
            {

                if (existingColumns.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                {

                    continue;

                }                    

                var bindingPath = GetColumnValueBindingName("Values", a.Key);

                grid.Columns.Add(new DataGridTextColumn()
                    {
                        Header = a.Name,
                        Binding = new Binding(bindingPath)
                            {
                                StringFormat = string.IsNullOrEmpty(a.Format) ? "{0}" : a.Format
                            },
                        SortMemberPath = bindingPath
                    });
            }
        }

        public void NodeDetailDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {

            if (m_NodeDetailAttributes == null)
            {

                return;

            }

            Grid grid = sender as Grid;

            int columns = 4;
            int rows = (int)Math.Ceiling((double)m_NodeDetailAttributes.Length / (double)columns);

            for (var i = 0; i < columns; i++)
            {

                grid.ColumnDefinitions.Add(new ColumnDefinition());

            }

            for (var i = 0; i < rows; i++)
            {

                grid.RowDefinitions.Add(new RowDefinition());

            }

            var k = 0;

            for (var i = 0; i < rows; i++)
            {

                for (var j = 0; j < columns; j++)
                {

                    var att = m_NodeDetailAttributes[k++];

                    StackPanel nameValuePanel = new StackPanel() { Orientation = Orientation.Horizontal };
                    TextBlock label = new TextBlock() { Style = App.Current.Resources["GlobalInfoLabel"] as Style, Text = att.Name + ":" };

                    nameValuePanel.Children.Add(label);

                    TextBlock value = new TextBlock();
                    value.Style = App.Current.Resources["GlobalInfoValue"] as Style;
                    value.SetBinding(TextBlock.TextProperty, new Binding(GetColumnValueBindingName("Values", att.Key))
                    {
                        StringFormat = string.IsNullOrEmpty(att.Format) ? "{0}" : att.Format
                    });
                    nameValuePanel.Children.Add(value);

                    nameValuePanel.SetValue(Grid.ColumnProperty, j);
                    nameValuePanel.SetValue(Grid.RowProperty, i);
                    grid.Children.Add(nameValuePanel);

                    if (k >= m_NodeDetailAttributes.Length)
                    {

                        break;

                    }
                        
                }

            }

        }

        private string GetColumnValueBindingName(string name)
        {

            return name;

        }

        private string GetColumnValueBindingName(string parent, string name)
        {

            return parent + "." + name;

        }

    }

}
