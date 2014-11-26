﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MyMediaLite.Data;
using MyMediaLite.ItemRecommendation;

namespace WrapRec.Recommenders
{
    public class MediaLitePosFeedbakItemRecommender : IPredictor<PositiveFeedback>, IUserItemMapper
    {
        ItemRecommender _itemRecommender;
        Mapping _usersMap;
        Mapping _itemsMap;
        bool _isTrained;

        public int NumRecommendations { get; set; }

        public MediaLitePosFeedbakItemRecommender(ItemRecommender itemRecommender)
            : this(itemRecommender, -1)
        { }

        public MediaLitePosFeedbakItemRecommender(ItemRecommender itemRecommender, int numRecommendations)
        {
            _itemRecommender = itemRecommender;
            _usersMap = new Mapping();
            _itemsMap = new Mapping();
            NumRecommendations = numRecommendations;
        }
        
        public void Train(IEnumerable<PositiveFeedback> trainSet)
        {
            Console.WriteLine("Training...");

            _itemRecommender.Feedback = trainSet.ToPosOnlyFeedback(_usersMap, _itemsMap);
            _itemRecommender.Train();

            _isTrained = true;
        }

        public void Predict(ItemRanking sample)
        {
            sample.PredictedRank = _itemRecommender.Predict(_usersMap.ToInternalID(sample.User.Id), _itemsMap.ToInternalID(sample.Item.Id));
        }

        public bool IsTrained
        {
            get { return _isTrained; }
        }

        public Mapping ItemsMap 
        {
            get { return _itemsMap; } 
        }

        public Mapping UsersMap
        {
            get { return _usersMap; }
        }

    }
}
