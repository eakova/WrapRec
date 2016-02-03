﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Reflection;
using WrapRec.Models;
using WrapRec.Data;
using WrapRec.Evaluation;
using System.Globalization;
using System.IO;
using WrapRec.Utils;
using WrapRec.IO;

namespace WrapRec.Core
{
    public class ExperimentManager
    {
        public XElement ConfigRoot { get; private set; }
		public IEnumerable<Experiment> Experiments { get; private set; }
		public Dictionary<string, DataContainer> DataContainers { get; private set; }
		public Dictionary<string, EvaluationContext> EvaluationContexts { get; private set; }
		public string ResultSeparator { get; private set; }
		public string ResultsFolder { get; set; }

		StreamWriter _resultWriter;
		string _lastModelId;
		string _lastExpId;

        public ExperimentManager(string configFile)
        {
            ConfigRoot = XDocument.Load(configFile).Root;
			DataContainers = new Dictionary<string, DataContainer>();
			EvaluationContexts = new Dictionary<string, EvaluationContext>();
			Setup();
        }

		public void RunExperiments()
		{
			int numExperiments = Experiments.Count();
			Logger.Current.Info("Number of experiment cases to be done: {0}", numExperiments);
			int caseNo = 1;

			foreach (Experiment e in Experiments)
			{
				try
				{
					Logger.Current.Info("\nCase {0} of {1}:\n----------------------------------------", caseNo++, numExperiments);
					e.Setup();
					LogExperimentInfo(e);
					e.Run();
					LogExperimentResults(e);
					WriteResultsToFile(e);
					e.Clear();
				}
				catch (Exception ex)
				{
					Logger.Current.Error("Error in expriment {0}, model {1}, split {2}:\n{1}", e.Id, e.Model.Id, e.Split.Id, ex.Message);
				}
			}
			
			if (_resultWriter != null)
				_resultWriter.Close();
		}


		private void Setup()
		{
			try
			{
				XElement allExpEl = ConfigRoot.Descendants("experiments").Single();

				if (allExpEl.Attribute("verbosity") != null && allExpEl.Attribute("verbosity").Value.ToLower() == "trace")
					Logger.Current = NLog.LogManager.GetLogger("traceLogger");

				ResultSeparator = allExpEl.Attribute("separator") != null ? allExpEl.Attribute("separator").Value : ",";
				
				string expFolder = DateTime.Now.ToString("wr yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture);
				string rootPath = allExpEl.Attribute("resultsFolder") != null ? allExpEl.Attribute("resultsFolder").Value 
					: Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
				ResultsFolder = Directory.CreateDirectory(Path.Combine(rootPath, expFolder)).FullName;

				XAttribute runAttr = allExpEl.Attribute("run");

				IEnumerable<XElement> expEls = allExpEl.Descendants("experiment");
				if (runAttr != null)
				{
					var expIds = runAttr.Value.Split(',');
					expEls = expEls.Where(el => expIds.Contains(el.Attribute("id").Value));
				}

				Logger.Current.Info("Resolving experiments...");
				Experiments = expEls.SelectMany(el => ParseExperiments(el));
			}
			catch (Exception ex)
			{
				Logger.Current.Error("Error in parsing the configuration file: {0}\n", ex.Message);
				Environment.Exit(1);
			}
		}

        private IEnumerable<Experiment> ParseExperiments(XElement expEl)
        {
			string expId = expEl.Attribute("id").Value;
			string expClass = expEl.Attribute("class") != null ? expEl.Attribute("class").Value : "";

			Type expType;
			if (!string.IsNullOrEmpty(expClass))
			{
				expType = Type.GetType(expClass, true);
				if (!typeof(Experiment).IsAssignableFrom(expType))
					throw new WrapRecException(string.Format("Experiment type '{0}' should inherit class 'WrapRec.Core.Experiment'", expClass));
			}
			else
				expType = typeof(Experiment);

			// one modelId might map to multiple models (if multiple values are used for parameters)
			IEnumerable<Model> models = expEl.Attribute("models").Value.Split(',')
				.SelectMany(mId => ParseModelsSet(mId));

			// one splitId always map to one split
			IEnumerable<Split> splits = expEl.Attribute("splits").Value.Split(',')
				.Select(sId => ParseSplit(sId));

			foreach (Split s in splits)
			{
				foreach (Model m in models)
				{
                    // if split has subslits (such as cross-validation) for each subsplit a new experiment instance is created
                    if (s.SubSplits != null && s.SubSplits.Count() > 0)
                    {
                        foreach (Split ss in s.SubSplits)
                        {
                            var exp = (Experiment)expType.GetConstructor(Type.EmptyTypes).Invoke(null);

                            exp.Model = m;
                            exp.Split = ss;
                            exp.EvaluationContext = GetEvaluationContext(expEl.Attribute("evalContext").Value);
                            exp.Id = expId;

							yield return exp;
                        }
                    }
                    else
                    {
                        var exp = (Experiment)expType.GetConstructor(Type.EmptyTypes).Invoke(null);

                        exp.Model = m;
                        exp.Split = s;
                        exp.EvaluationContext = GetEvaluationContext(expEl.Attribute("evalContext").Value);
                        exp.Id = expId;

						yield return exp;
                    }
				}
			}
        }

        private IEnumerable<Model> ParseModelsSet(string modelId)
        {
            XElement modelEl = ConfigRoot.Descendants("model")
                .Where(el => el.Attribute("id").Value == modelId).Single();

            Type modelType = Type.GetType(modelEl.Attribute("class").Value, true);
            if (!typeof(Model).IsAssignableFrom(modelType))
                throw new WrapRecException(string.Format("Model type '{0}' should inherit class 'WrapRec.Models.Model'", modelType.Name));

            var allSetupParams = modelEl.Descendants("parameters").Single()
                .Attributes().ToDictionary(a => a.Name, a => a.Value);

			var paramCartesians = allSetupParams.Select(kv => kv.Value.Split(',').AsEnumerable()).CartesianProduct();

			foreach (IEnumerable<string> pc in paramCartesians)
			{
				var setupParams = allSetupParams.Select(kv => kv.Key)
					.Zip(pc, (k, v) => new { Name = k, Value = v })
					.ToDictionary(kv => kv.Name.LocalName, kv => kv.Value);

				var model = (Model)modelType.GetConstructor(Type.EmptyTypes).Invoke(null);
				model.SetupParameters = setupParams;

				yield return model;
			}
        }

        private Split ParseSplit(string splitId)
        {
            XElement splitEl = ConfigRoot.Descendants("split")
				.Where(el => el.Attribute("id").Value == splitId).Single();

			SplitType splitType = (SplitType) Enum.Parse(typeof(SplitType), splitEl.Attribute("type").Value.ToUpper());
			DataContainer container = GetDataContainer(splitEl.Attribute("dataContainer").Value);
			
			Split split;
			if (splitEl.Attribute("class") != null)
			{
				Type splitClassType = Type.GetType(splitEl.Attribute("class").Value);
				if (!typeof(Split).IsAssignableFrom(splitClassType))
					throw new WrapRecException(string.Format("Split type '{0}' should inherit class 'WrapRec.Data.Split'", splitClassType.Name));

				split = (Split) splitClassType.GetConstructor(Type.EmptyTypes).Invoke(null);
			}
			else
				split = new FeedbackSimpleSplit();

			var setupParams = splitEl.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);

			split.Id = splitId;
			split.Type = splitType;
			split.Container = container;
			split.SetupParameters = setupParams;

			return split;
        }

		private DataContainer GetDataContainer(string containerId)
		{
			if (DataContainers.ContainsKey(containerId))
				return DataContainers[containerId];

			XElement dcEl = ConfigRoot.Descendants("dataContainer")
				.Where(el => el.Attribute("id").Value == containerId).Single();

			var container = new DataContainer();
			container.Id = containerId;
			foreach (string readerId in dcEl.Attribute("dataReaders").Value.Split(','))
			{
				container.DataReaders.Add(ParseDataReader(readerId));
			}

			DataContainers.Add(containerId, container);
			return container;
		}

		private DatasetReader ParseDataReader(string readerId)
		{
			XElement readerEl = ConfigRoot.Descendants("reader")
				.Where(el => el.Attribute("id").Value == readerId).Single();

			FeedbackSlice sliceType = FeedbackSlice.NOT_APPLICABLE;
			XAttribute sliceTypeAttr = readerEl.Attribute("sliceType");
			if (sliceTypeAttr != null)
			{
				if (sliceTypeAttr.Value == "train")
					sliceType = FeedbackSlice.TRAIN;
				else if (sliceTypeAttr.Value == "test")
					sliceType = FeedbackSlice.TEST;
			}

			DataType dataType = DataType.Other;
			XAttribute dataTypeAttr = readerEl.Attribute("dataType");
			if (dataTypeAttr != null)
				dataType = (DataType)Enum.Parse(typeof(DataType), dataTypeAttr.Value);

			DatasetReader reader;
			if (readerEl.Attribute("class") != null)
			{
				Type readerClassType = Type.GetType(readerEl.Attribute("class").Value);
				if (!typeof(DatasetReader).IsAssignableFrom(readerClassType))
					throw new WrapRecException(string.Format("Reader type '{0}' should inherit class 'WrapRec.Data.DatasetReader'", readerClassType.Name));

				reader = (DatasetReader)readerClassType.GetConstructor(Type.EmptyTypes).Invoke(null);
			}
			else
				reader = new CsvReader();

			reader.Id = readerId;
			reader.Path = readerEl.Attribute("path").Value;
			reader.DataType = dataType;
			reader.SliceType = sliceType;
			reader.SetupParameters = readerEl.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);

			return reader;
		}

        private EvaluationContext GetEvaluationContext(string contextId)
        {
			if (EvaluationContexts.ContainsKey(contextId))
				return EvaluationContexts[contextId];
			
			XElement contextEl = ConfigRoot.Descendants("evalContext")
				.Where(el => el.Attribute("id").Value == contextId).Single();

			var ec = new EvaluationContext();
			ec.Id = contextEl.Attribute("id").Value;

			foreach (XElement evalEl in contextEl.Descendants("evaluator"))
			{
				Evaluator eval;
				Type evalType = Type.GetType(evalEl.Attribute("class").Value);
				if (!typeof(Evaluator).IsAssignableFrom(evalType))
					throw new WrapRecException(string.Format("Evaluator type '{0}' should inherit class 'WrapRec.Evaluation.Evaluator'", evalType.Name));

				eval = (Evaluator)evalType.GetConstructor(Type.EmptyTypes).Invoke(null);
				eval.SetupParameters = evalEl.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);				

				ec.AddEvaluator(eval);
			}

			EvaluationContexts.Add(contextId, ec);
			return ec;
        }

		

		private void LogExperimentInfo(Experiment exp)
		{
			string format = @"
Experiment Id: {0}
Split Id: {1}
Model Id: {2}
Model Parameteres:
{3}
";
			string modelParameters = exp.Model.GetModelParameters().Select(kv => kv.Key + ":" + kv.Value)
				.Aggregate((a, b) => a + " " + b);

			Logger.Current.Info(format, exp.Id, exp.Split.Id, exp.Model.Id, modelParameters);
		}

		private void LogExperimentResults(Experiment exp)
		{
			string format = @"\nResults:\n {0}\nTimes:\n Training: {1} Evaluation: {2}\n";

			string results = exp.EvaluationContext.Results.Select(kv => kv.Key + ":" + kv.Value)
				.Aggregate((a, b) => a + " " + b);

			Logger.Current.Info(format, results, exp.TrainTime, exp.EvaluationTime);
		}

		private void WriteResultsToFile(Experiment exp)
		{
			if (_lastExpId != exp.Id)
			{
				if (_resultWriter != null)
					_resultWriter.Close();

				_resultWriter = new StreamWriter(Path.Combine(ResultsFolder, exp.Id + ".csv"));
				_lastExpId = exp.Id;
			}
			
			// write a header to the csv file if the model is changed (different models have different parameters)
			if (_lastModelId != exp.Model.Id)
			{
				string header = new string[] { "ExpeimentId", "ModelId", "SplitId" }
					.Concat(exp.Model.GetModelParameters().Select(kv => kv.Key))
					.Concat(exp.EvaluationContext.Results.Select(kv => kv.Key))
					.Concat(new string[] { "TrainTime", "EvaluationTime", "PureTrainTime", "PureEvaluationTime", "TotalTime", "PureTotalTime" })
					.Aggregate((a, b) => a + ResultSeparator + b);

				_resultWriter.WriteLine(header);
				_lastModelId = exp.Model.Id;
			}

			string result = new string[] { exp.Id, exp.Model.Id, exp.Split.Id }
				.Concat(exp.Model.GetModelParameters().Select(kv => kv.Value))
				.Concat(exp.EvaluationContext.Results.Select(kv => kv.Value))
				.Concat(new string[] { exp.TrainTime.ToString(), exp.EvaluationTime.ToString(), exp.Model.PureTrainTime.ToString(), exp.Model.PureEvaluationTime.ToString(), 
					(exp.TrainTime + exp.EvaluationTime).ToString(), (exp.Model.PureTrainTime + exp.Model.PureEvaluationTime).ToString() })
				.Aggregate((a, b) => a + ResultSeparator + b);

			_resultWriter.WriteLine(result);
			_resultWriter.Flush();
		}

    }
}
