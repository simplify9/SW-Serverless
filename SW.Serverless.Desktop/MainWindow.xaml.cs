using Microsoft.Win32;
using Newtonsoft.Json;
using SW.Serverless.Tooling;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SW.Serverless.Desktop
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private IDictionary<string, CloudConnection> connections;
        private CloudConnection chosenConnection;
        public string chosenAdapterPath { get; set; } = string.Empty;
        private Options options;
        private InstallerLogic installer;

        public MainWindow()
        {
            InitializeComponent();
            installer = new InstallerLogic();
            initConnections();
        }

        private void addConnection(CloudConnection con)
        {
            if (connections.Count == 0)
            {
                connectionListBox.Items.Clear();
            }

            var key = $"{con.ServiceUrl}";
            var protoIndex = key.LastIndexOf("://");
            if (protoIndex != -1)
            {
                key = key.Insert(protoIndex + 3, $"{con.BucketName}.");
            }

            if (connections.TryAdd(key, con))
            {
                connectionListBox.Items.Add(new ListBoxItem { Content = key });
            }
        }

        private void initConnections()
        {
            this.options = GetOptionsFromJson();
            connections = new Dictionary<string, CloudConnection>();
            if (options.CloudConnections.Any())
                foreach (var con in options.CloudConnections)
                    addConnection(con);
            else
                connectionListBox.Items.Add(new TextBlock { Text = "Add connection using below menu" });
        }

        private void chooseConnection(object sender, RoutedEventArgs e)
        {
            if (connectionListBox.SelectedItem is ListBoxItem item)
            {
                string key = item.Content.ToString();
                chosenConnection = connections[key];
            }
        }

        private void addConnectionToJson(object sender, RoutedEventArgs e)
        {
            Options current = GetOptionsFromJson();
            var connection = new CloudConnection
            {
                AccessKeyId = accessKeyText.Text,
                BucketName = bucketNameText.Text,
                SecretAccessKey = secretAccessText.Text,
                ServiceUrl = serviceUrlText.Text
            };
            current.CloudConnections.Add(connection);
            string optionsJson = JsonConvert.SerializeObject(current);
            File.WriteAllText("./settings.json", optionsJson);
            addConnection(connection);
        }

        private Options GetOptionsFromJson()
        {
            if (File.Exists("./settings.json"))
            {
                string optionsJson = File.ReadAllText("./settings.json");
                return JsonConvert.DeserializeObject<Options>(optionsJson);
            }
            else
            {
                File.WriteAllText("./settings.json", JsonConvert.SerializeObject(new Options()));
                return new Options();
            }
        }

        private async void installAdapter(object sender, RoutedEventArgs e)
        {
            string adapterId = adapterIdText.Text;
            string projectPath = chosenAdapterPath;
            if (chosenConnection == null)
            {
                errors.Text = $"\nSelect a connection.";
                return;
            }

            if (string.IsNullOrEmpty(chosenAdapterPath))
            {
                errors.Text = $"\nBrowse or paste adapter path above";
                return;
            }

            if (string.IsNullOrEmpty(adapterId))
            {
                errors.Text = $"\nAdapter Id is required.";
                return;
            }

            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            if (!installer.BuildPublish(projectPath, tempPath))
            {
                errors.Text = $"Build failed. Check adapter at {chosenAdapterPath}.";
                return;
            }

            var zipFileName = System.IO.Path.Combine(tempPath, $"{adapterId}");

            if (!installer.Compress(tempPath, zipFileName))
            {
                errors.Text = "Compression failed.";
                return;
            }

            var entryAssembly = InstallerLogic.ResolveEntryAssembly(tempPath, projectPath);
            if (entryAssembly == null)
            {
                errors.Text = "The adapter's entry assembly is not in the build output.";
                installer.Cleanup(tempPath);
                return;
            }
            var cloudOptions = new Installer.ServerlessUploadOptions
            {
                AccessKeyId = chosenConnection.AccessKeyId,
                SecretAccessKey = chosenConnection.SecretAccessKey,
                ServiceUrl = chosenConnection.ServiceUrl,
                BucketName = chosenConnection.BucketName,
                AdapterId = adapterId
            };
            
            if (await installer.PushToCloud(zipFileName, entryAssembly, cloudOptions))
            {
                errors.Text = "Install successful. You can install another adapter.";
            }
            else
            {
                errors.Text = "Install failed, check configuration.";
            }

            if (!installer.Cleanup(tempPath))
            {
                errors.Text = "Cleanup Failed. Check remaining files.";
            }
        }

        private void chooseAdapter(object sender, RoutedEventArgs args)
        {
            var dialogue = new OpenFileDialog();
            dialogue.Multiselect = false;
            dialogue.CheckFileExists = true;
            dialogue.ValidateNames = true;
            dialogue.ShowDialog();
            if (dialogue.FileNames != null && dialogue.FileNames.Length > 0)
            {
                chosenAdapterPath = dialogue.FileName;
                adapterPathText.Text = dialogue.FileName;
            }
        }

        private void adapterPathText_TextInput(object sender, TextCompositionEventArgs e)
        {
            chosenAdapterPath += e.Text;
        }
    }
}
